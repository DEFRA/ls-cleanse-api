namespace Cleanse.Core.Domain.Mi;

/// <summary>
/// Builds a group's summary by replaying its history: every issue from when it was created, each
/// audit entry that moved it, the end-of-run sweeps, and the actions and notes on its holdings. A
/// holding's status is worked out again after every change to one of its issues, with the same
/// rule the rest of the service uses, so the summary can say how many holdings were in each state
/// on any day, and who closed them.
/// </summary>
/// <remarks>
/// The sweep does not audit each issue it deactivates, but nothing is lost: an issue keeps when it
/// was last deactivated, and each reactivation's entry records the deactivation it undid.
/// </remarks>
public static class MiProjector
{
    /// <summary>Bump when the way the summary is worked out changes, so every group is rebuilt.</summary>
    public const int Version = 1;

    public static MiSummary Build(
        MiHistory history,
        IReadOnlyDictionary<Guid, string> people,
        DateTimeOffset? lastResetAtUtc,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(people);
        return new Replay(history, people).Run(lastResetAtUtc, now);
    }

    /// <summary>
    /// Also the order of things that happen at the same instant: an issue exists before anything
    /// happens to it, and actions recorded as a holding is closed count towards closing it.
    /// </summary>
    private enum StepType
    {
        Created,
        Deactivated,
        Reactivated,
        Action,
        Note,
        Workflow
    }

    private readonly record struct Step(
        DateTimeOffset At,
        StepType Type,
        long Order,
        IssueSim? Issue,
        MiAuditFact? Fact,
        MiHistoryActivity? Activity);

    private sealed class IssueSim(MiHistoryIssue row, HoldingSim? holding)
    {
        public MiHistoryIssue Row { get; } = row;

        public HoldingSim? Holding { get; } = holding;

        public bool Exists { get; set; }

        public IssueStatus Status { get; set; } = IssueStatus.Active;

        public WorkflowStatus Workflow { get; set; } = WorkflowStatus.Todo;

        public Guid? ReasonId { get; set; }

        public string? ReasonName { get; set; }

        public DateTimeOffset LastChangedAt { get; set; }

        public bool IsOpen => Exists && Status == IssueStatus.Active &&
                              Workflow is WorkflowStatus.Todo or WorkflowStatus.InProgress;
    }

    private sealed class HoldingSim(string cph)
    {
        public string Cph { get; } = cph;

        public List<IssueSim> Issues { get; } = [];

        public bool Exists { get; set; }

        public HoldingStatus Status { get; set; } = HoldingStatus.Closed;

        public bool HasActive { get; set; }

        public DateTimeOffset? InProgressSince { get; set; }

        public DateTimeOffset? LastWorkedAt { get; set; }

        public int ActionsSinceClosed { get; set; }

        public bool IsOpen => Exists && Status is HoldingStatus.Todo or HoldingStatus.InProgress;
    }

    private sealed class Replay(MiHistory history, IReadOnlyDictionary<Guid, string> people)
    {
        private readonly Dictionary<string, IssueSim> _issues = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HoldingSim> _holdings = new(StringComparer.Ordinal);
        private readonly List<MiEvent> _events = [];
        private readonly Dictionary<(DateOnly Day, MiUnit Unit, string Rule, string Status, string State), int> _daily = [];
        private readonly Lazy<Dictionary<string, MiHistoryHolding>> _assignments = new(() =>
            history.Holdings
                .GroupBy(holding => holding.Cph, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal));
        private readonly List<string> _mismatched = [];

        public MiSummary Run(DateTimeOffset? lastResetAtUtc, DateTimeOffset now)
        {
            foreach (var step in Steps())
            {
                Apply(step);
            }

            CorrectToCurrentState();

            var issueStates = _issues.Values.Where(issue => issue.Exists).Select(ToState).ToList();
            var holdingStates = _holdings.Values.Where(holding => holding.Exists).Select(ToState).ToList();
            var events = WithInactiveNow();
            var daily = _daily
                .Where(entry => entry.Value != 0)
                .Select(entry => new MiDailyChange(
                    history.GroupId, entry.Key.Day, entry.Key.Unit, entry.Key.Rule, entry.Key.Status, entry.Key.State, entry.Value))
                .ToList();

            var historyFrom = lastResetAtUtc ?? history.FirstRunStartedAtUtc ??
                              (history.Issues.Count == 0 ? null : history.Issues.Min(issue => issue.CreatedAtUtc));

            var refresh = new MiRefreshRecord(
                history.GroupId, now, now, history.Fingerprint, Version, 0, historyFrom,
                issueStates.Count, events.Count, _mismatched.Count);

            return new MiSummary(refresh, issueStates, holdingStates, events, daily) { MismatchedIssueIds = _mismatched };
        }

        private List<Step> Steps()
        {
            var steps = new List<Step>(history.Issues.Count * 2 + history.AuditFacts.Count + history.Activity.Count);

            foreach (var row in history.Issues)
            {
                var holding = string.IsNullOrWhiteSpace(row.Cph) ? null : HoldingFor(row.Cph);
                var issue = new IssueSim(row, holding);
                _issues[row.Id] = issue;
                holding?.Issues.Add(issue);

                steps.Add(new Step(row.CreatedAtUtc, StepType.Created, 0, issue, null, null));
                if (row.Status == IssueStatus.Inactive && row.DeactivatedAtUtc is { } deactivated)
                {
                    steps.Add(new Step(Later(deactivated, row), StepType.Deactivated, long.MaxValue, issue, null, null));
                }
            }

            foreach (var fact in history.AuditFacts)
            {
                if (!_issues.TryGetValue(fact.IssueId, out var issue))
                {
                    continue;
                }

                if (fact.Kind == AuditEventKind.IssueReactivated)
                {
                    if (fact.DeactivatedFrom is { } deactivated)
                    {
                        steps.Add(new Step(Later(deactivated, issue.Row), StepType.Deactivated, fact.Order, issue, null, null));
                    }

                    steps.Add(new Step(Later(fact.AtUtc, issue.Row), StepType.Reactivated, fact.Order, issue, fact, null));
                    continue;
                }

                steps.Add(new Step(Later(fact.AtUtc, issue.Row), StepType.Workflow, fact.Order, issue, fact, null));
            }

            foreach (var activity in history.Activity)
            {
                var type = activity.Unit == MiUnit.Action ? StepType.Action : StepType.Note;
                steps.Add(new Step(activity.AtUtc, type, activity.Order, null, null, activity));
            }

            // The type breaks ties, so an issue exists before anything happens to it at the same instant.
            steps.Sort((a, b) =>
            {
                var byTime = a.At.CompareTo(b.At);
                if (byTime != 0)
                {
                    return byTime;
                }

                var byType = a.Type.CompareTo(b.Type);
                return byType != 0 ? byType : a.Order.CompareTo(b.Order);
            });

            return steps;
        }

        /// <summary>Nothing happens to an issue before the rules first found it.</summary>
        private static DateTimeOffset Later(DateTimeOffset at, MiHistoryIssue row) =>
            at < row.CreatedAtUtc ? row.CreatedAtUtc : at;

        private HoldingSim HoldingFor(string cph)
        {
            if (!_holdings.TryGetValue(cph, out var holding))
            {
                _holdings[cph] = holding = new HoldingSim(cph);
            }

            return holding;
        }

        private void Apply(Step step)
        {
            switch (step.Type)
            {
                case StepType.Created:
                    Create(step.Issue!, step.At);
                    break;
                case StepType.Deactivated:
                    SetActive(step.Issue!, active: false, step.At);
                    break;
                case StepType.Reactivated:
                    SetActive(step.Issue!, active: true, step.At);
                    break;
                case StepType.Workflow:
                    ChangeWorkflow(step.Issue!, step.Fact!, step.At);
                    break;
                default:
                    Record(step.Activity!);
                    break;
            }
        }

        private void Create(IssueSim issue, DateTimeOffset at)
        {
            issue.Exists = true;
            issue.LastChangedAt = at;

            EmitIssue(issue, at, MiEventKind.Raised, MiSource.Rules, null, null, null, MiOpenChange.Raised);
            Count(at, issue, issue.Status, issue.Workflow, +1);
            UpdateHolding(issue.Holding, at, MiSource.Rules, null, MiOpenChange.Raised);
        }

        private void SetActive(IssueSim issue, bool active, DateTimeOffset at)
        {
            var target = active ? IssueStatus.Active : IssueStatus.Inactive;
            if (!issue.Exists || issue.Status == target)
            {
                return;
            }

            var wasOpen = issue.IsOpen;
            var before = issue.Status;
            issue.Status = target;
            issue.LastChangedAt = at;

            MiOpenChange? change = wasOpen == issue.IsOpen
                ? null
                : active ? MiOpenChange.CameBack : MiOpenChange.NoLongerFound;
            EmitIssue(issue, at, active ? MiEventKind.CameBack : MiEventKind.NoLongerFound, MiSource.Rules, null, before, issue.Workflow, change);
            Count(at, issue, before, issue.Workflow, -1);
            Count(at, issue, issue.Status, issue.Workflow, +1);
            UpdateHolding(issue.Holding, at, MiSource.Rules, null, change);
        }

        private void ChangeWorkflow(IssueSim issue, MiAuditFact fact, DateTimeOffset at)
        {
            if (!issue.Exists)
            {
                return;
            }

            var source = fact.Kind switch
            {
                AuditEventKind.IssueSuppressionExpired => MiSource.Expiry,
                AuditEventKind.IssueExternalStateApplied => MiSource.Spol,
                _ => MiSource.Cleanse
            };

            var to = fact.WorkflowTo ?? issue.Workflow;
            var (reasonId, reasonName) = fact.ReasonChanged
                ? (fact.ReasonId, fact.ReasonName)
                : to.IsOutcome() && to == issue.Workflow ? (issue.ReasonId, issue.ReasonName) : (null, null);

            if (to == issue.Workflow)
            {
                // A new reason for the same outcome is not a new outcome.
                issue.ReasonId = reasonId;
                issue.ReasonName = reasonName;
                return;
            }

            var person = source == MiSource.Cleanse ? PersonOf(fact.Actor) : null;
            var wasOpen = issue.IsOpen;
            var before = issue.Workflow;
            issue.Workflow = to;
            issue.ReasonId = reasonId;
            issue.ReasonName = reasonName;
            issue.LastChangedAt = at;

            var change = OpenChangeOf(wasOpen, issue.IsOpen, source, before, to);
            EmitIssue(issue, at, MiEventKind.Workflow, source, person, issue.Status, before, change);
            Count(at, issue, issue.Status, before, -1);
            Count(at, issue, issue.Status, to, +1);

            if (source == MiSource.Cleanse && issue.Holding is { } holding)
            {
                holding.LastWorkedAt = at;
            }

            UpdateHolding(issue.Holding, at, source, person, change);
        }

        private static MiOpenChange? OpenChangeOf(
            bool wasOpen, bool isOpen, MiSource source, WorkflowStatus before, WorkflowStatus after)
        {
            if (wasOpen == isOpen)
            {
                return null;
            }

            if (source == MiSource.Spol)
            {
                return MiOpenChange.Spol;
            }

            if (isOpen)
            {
                return before switch
                {
                    WorkflowStatus.Resolved or WorkflowStatus.Nonactionable => MiOpenChange.Reopened,
                    WorkflowStatus.Suppressed => MiOpenChange.SuppressionEnded,
                    _ => MiOpenChange.Correction
                };
            }

            return after switch
            {
                WorkflowStatus.Resolved => MiOpenChange.Resolved,
                WorkflowStatus.Nonactionable => MiOpenChange.CantResolve,
                WorkflowStatus.Suppressed => MiOpenChange.Suppressed,
                _ => MiOpenChange.Correction
            };
        }

        private void Record(MiHistoryActivity activity)
        {
            var person = PersonOf(activity.CreatedBy);
            var isAction = activity.Unit == MiUnit.Action;

            Emit(new MiEvent(
                history.GroupId, 0, activity.AtUtc, MiCalendar.UkDay(activity.AtUtc), activity.Unit,
                isAction ? MiEventKind.Action : MiEventKind.Note, MiSource.Cleanse,
                null, null, activity.Cph, person?.Id, person?.Name,
                null, null, null, null, activity.ActionTypeId, activity.ActionTypeName, null, 0));

            if (_holdings.TryGetValue(activity.Cph, out var holding) && holding.Exists)
            {
                holding.LastWorkedAt = activity.AtUtc;
                if (isAction)
                {
                    holding.ActionsSinceClosed++;
                }
            }
        }

        private void UpdateHolding(
            HoldingSim? holding, DateTimeOffset at, MiSource source, (Guid Id, string Name)? person, MiOpenChange? cause)
        {
            if (holding is null)
            {
                return;
            }

            var present = holding.Issues.Where(issue => issue.Exists).ToList();
            var status = HoldingStatuses.Derive(present.Select(issue => (issue.Status, issue.Workflow)));
            var hasActive = present.Any(issue => issue.Status == IssueStatus.Active);
            if (holding.Exists && holding.Status == status && holding.HasActive == hasActive)
            {
                return;
            }

            var wasOpen = holding.IsOpen;
            var isOpen = status is HoldingStatus.Todo or HoldingStatus.InProgress;
            MiOpenChange? change = wasOpen == isOpen ? null : cause ?? MiOpenChange.Correction;

            int? actionsSinceClosed = null;
            if (holding.Exists && holding.Status != HoldingStatus.Closed && status == HoldingStatus.Closed)
            {
                actionsSinceClosed = holding.ActionsSinceClosed;
                holding.ActionsSinceClosed = 0;
            }

            Emit(new MiEvent(
                history.GroupId, 0, at, MiCalendar.UkDay(at), MiUnit.Holding, MiEventKind.HoldingStatus, source,
                null, null, holding.Cph, person?.Id, person?.Name,
                holding.Exists ? MiNames.ToStatusWire(holding.HasActive) : null,
                MiNames.ToStatusWire(hasActive),
                holding.Exists ? holding.Status.ToWire() : null,
                status.ToWire(),
                null, null, change, change is null ? 0 : isOpen ? 1 : -1,
                ActionsSinceClosed: actionsSinceClosed));

            var day = MiCalendar.UkDay(at);
            if (holding.Exists)
            {
                Add(day, MiUnit.Holding, string.Empty, MiNames.ToStatusWire(holding.HasActive), holding.Status.ToWire(), -1);
            }

            Add(day, MiUnit.Holding, string.Empty, MiNames.ToStatusWire(hasActive), status.ToWire(), +1);

            if (status != HoldingStatus.InProgress)
            {
                holding.InProgressSince = null;
            }
            else if (!holding.Exists || holding.Status != HoldingStatus.InProgress)
            {
                holding.InProgressSince = at;
            }

            holding.Exists = true;
            holding.Status = status;
            holding.HasActive = hasActive;
        }

        /// <summary>
        /// Puts right an issue whose history does not lead to what it is now, from when it last
        /// changed. Counted, because it means a change was made without an audit entry.
        /// </summary>
        private void CorrectToCurrentState()
        {
            foreach (var issue in _issues.Values.Where(issue => issue.Exists))
            {
                var row = issue.Row;
                if (issue.Status == row.Status && issue.Workflow == row.WorkflowStatus && issue.ReasonId == row.ReasonId)
                {
                    continue;
                }

                _mismatched.Add(row.Id);
                var at = row.UpdatedAtUtc > issue.LastChangedAt ? row.UpdatedAtUtc : issue.LastChangedAt;
                issue.ReasonId = row.ReasonId;
                issue.ReasonName = row.ReasonName;
                if (issue.Status == row.Status && issue.Workflow == row.WorkflowStatus)
                {
                    continue;
                }

                var wasOpen = issue.IsOpen;
                var (status, workflow) = (issue.Status, issue.Workflow);
                issue.Status = row.Status;
                issue.Workflow = row.WorkflowStatus;
                issue.LastChangedAt = at;

                MiOpenChange? change = wasOpen == issue.IsOpen ? null : MiOpenChange.Correction;
                EmitIssue(issue, at, MiEventKind.Corrected, MiSource.Correction, null, status, workflow, change);
                Count(at, issue, status, workflow, -1);
                Count(at, issue, issue.Status, issue.Workflow, +1);
                UpdateHolding(issue.Holding, at, MiSource.Correction, null, change);
            }
        }

        private void EmitIssue(
            IssueSim issue,
            DateTimeOffset at,
            MiEventKind kind,
            MiSource from,
            (Guid Id, string Name)? person,
            IssueStatus? statusBefore,
            WorkflowStatus? workflowBefore,
            MiOpenChange? change)
        {
            var outcome = kind == MiEventKind.Workflow && issue.Workflow.IsOutcome();
            Emit(new MiEvent(
                history.GroupId, 0, at, MiCalendar.UkDay(at), MiUnit.Issue, kind, from,
                issue.Row.Id, issue.Row.RuleId, issue.Holding?.Cph, person?.Id, person?.Name,
                statusBefore?.ToWire(), issue.Status.ToWire(),
                workflowBefore?.ToWire(), issue.Workflow.ToWire(),
                outcome ? issue.ReasonId : null, outcome ? issue.ReasonName : null,
                change, change is null ? 0 : issue.IsOpen ? 1 : -1,
                ElapsedDays: outcome ? Math.Max(0, (int)Math.Floor((at - issue.Row.CreatedAtUtc).TotalDays)) : null));
        }

        private void Emit(MiEvent e) => _events.Add(e with { Sequence = _events.Count + 1 });

        private void Count(DateTimeOffset at, IssueSim issue, IssueStatus status, WorkflowStatus workflow, int delta) =>
            Add(MiCalendar.UkDay(at), MiUnit.Issue, issue.Row.RuleId, status.ToWire(), workflow.ToWire(), delta);

        private void Add(DateOnly day, MiUnit unit, string rule, string status, string state, int delta)
        {
            var key = (day, unit, rule, status, state);
            _daily[key] = _daily.GetValueOrDefault(key) + delta;
        }

        /// <summary>A change counts for a person only when it was recorded against a known user.</summary>
        private (Guid Id, string Name)? PersonOf(Actor actor) =>
            actor.Kind == ActorKind.Human &&
            Guid.TryParse(actor.Id, out var id) &&
            people.TryGetValue(id, out var name)
                ? (id, name)
                : null;

        private List<MiEvent> WithInactiveNow()
        {
            for (var i = 0; i < _events.Count; i++)
            {
                if (_events[i].IssueId is { } id && _issues.TryGetValue(id, out var issue))
                {
                    _events[i] = _events[i] with { InactiveNow = issue.Status == IssueStatus.Inactive };
                }
            }

            return _events;
        }

        private MiIssueState ToState(IssueSim issue) => new(
            history.GroupId,
            issue.Row.Id,
            issue.Row.RuleId,
            issue.Holding?.Cph,
            issue.Status,
            issue.Workflow,
            issue.ReasonId,
            issue.ReasonName,
            issue.Row.CreatedAtUtc,
            MiCalendar.UkDay(issue.Row.CreatedAtUtc),
            issue.LastChangedAt);

        private MiHoldingState ToState(HoldingSim holding)
        {
            var open = holding.Issues.Where(issue => issue.IsOpen).ToList();
            DateTimeOffset? oldest = open.Count == 0 ? null : open.Min(issue => issue.Row.CreatedAtUtc);
            var assignment = _assignments.Value.GetValueOrDefault(holding.Cph);
            var assigneeName = assignment?.AssignedUserId is { } userId && people.TryGetValue(userId, out var name)
                ? name
                : assignment?.AssignedUserFullName ?? assignment?.AssignedTo;

            return new MiHoldingState(
                history.GroupId,
                holding.Cph,
                holding.Status,
                holding.HasActive,
                open.Count,
                oldest,
                oldest is { } at ? MiCalendar.UkDay(at) : null,
                holding.InProgressSince,
                holding.LastWorkedAt,
                assignment?.AssignedUserId,
                string.IsNullOrWhiteSpace(assigneeName) ? null : assigneeName);
        }
    }
}
