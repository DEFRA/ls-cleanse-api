using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Mi;

namespace Cleanse.Core.Queries.Mi;

/// <summary>
/// The state of the group's issues, or holdings, over a period, whoever changed them: how many
/// were in each status at each point, how the rules' findings moved, and every reason the number
/// open went up or down. Issues are the default unit; <c>holdings</c> counts holdings instead.
/// </summary>
public sealed record GetMiBacklogQuery(
    string GroupId,
    DateOnly? From = null,
    DateOnly? To = null,
    string? Interval = null,
    string? RuleId = null,
    string? Unit = null) : IQuery<MiBacklogDto>;

internal sealed class GetMiBacklogQueryHandler(
    IMiStore store,
    ISettingsStore settings,
    IImportJobStore imports,
    TimeProvider clock,
    CleanseCoreOptions options) : IQueryHandler<GetMiBacklogQuery, MiBacklogDto>
{
    public const string Issues = "issues";
    public const string Holdings = "holdings";

    private const int OldestShown = 10;
    private const int StalledShown = 50;
    private const int StalledCounted = 5000;

    private static readonly string[] IssueStates =
        [StatusNames.Todo, StatusNames.InProgress, StatusNames.Suppressed, StatusNames.Resolved, StatusNames.Nonactionable];

    private static readonly string[] HoldingStates =
        [HoldingStatuses.Todo, HoldingStatuses.InProgress, HoldingStatuses.Suppressed, HoldingStatuses.Closed];

    private static readonly MiOpenChange[] WaterfallOrder =
    [
        MiOpenChange.Raised, MiOpenChange.CameBack, MiOpenChange.Reopened, MiOpenChange.SuppressionEnded,
        MiOpenChange.Resolved, MiOpenChange.CantResolve, MiOpenChange.Suppressed, MiOpenChange.NoLongerFound,
        MiOpenChange.Spol, MiOpenChange.Correction
    ];

    public async Task<Result<MiBacklogDto>> HandleAsync(GetMiBacklogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (MiReadModel.CheckGroup(query.GroupId) is { } invalid)
        {
            return invalid;
        }

        var unitName = string.IsNullOrWhiteSpace(query.Unit) ? Issues : query.Unit.Trim().ToLowerInvariant();
        if (unitName is not (Issues or Holdings))
        {
            return DomainErrors.Validation($"'{query.Unit}' is not a unit. Use issues or holdings.");
        }

        var ruleId = string.IsNullOrWhiteSpace(query.RuleId) ? null : query.RuleId.Trim();
        var unit = unitName == Holdings ? MiUnit.Holding : MiUnit.Issue;
        if (unit == MiUnit.Holding && ruleId is not null)
        {
            return DomainErrors.Validation(
                "Holdings cannot be split by rule, because a holding can have issues for several. Count issues, or every rule.");
        }

        var groupId = query.GroupId.Trim();
        var now = clock.GetUtcNow();
        var today = MiCalendar.UkDay(now);
        var refresh = await store.GetRefreshAsync(groupId, cancellationToken);
        var historyFrom = MiReadModel.HistoryFromDay(refresh);

        var resolved = MiPeriod.Resolve(query.From, query.To, query.Interval, today, historyFrom);
        if (resolved.IsFailure)
        {
            return resolved.Error;
        }

        var period = resolved.Value;
        var buckets = period.Buckets();
        var states = unit == MiUnit.Issue ? IssueStates : HoldingStates;

        var daily = await store.GetDailyChangesAsync(groupId, unit, ruleId, period.To, cancellationToken);
        var (atStart, atEnds) = RunningTotals(daily, period.From, buckets);

        var events = await store.CountEventsAsync(
            new MiEventFilter(groupId, period.From, period.To) { Units = [unit], RuleId = ruleId },
            MiEventDimensions.Day | MiEventDimensions.Kind | MiEventDimensions.FromStatus | MiEventDimensions.ToStatus |
            MiEventDimensions.OpenChange | MiEventDimensions.OpenDelta,
            cancellationToken);

        var holdingCounts = await store.CountHoldingStatesAsync(groupId, cancellationToken);
        IReadOnlyList<MiIssueStateCount> issueCounts = unit == MiUnit.Issue
            ? await store.CountIssueStatesAsync(groupId, ruleId, cancellationToken)
            : [];
        var minimumAge = (await settings.GetAsync(cancellationToken) ?? ServiceSettings.Default).MinimumIssueAgeDays;

        var oldest = await store.ListHoldingStatesAsync(
            new MiHoldingListQuery(groupId, MiHoldingList.OldestOpen, OldestShown), cancellationToken);
        var stalled = await store.ListHoldingStatesAsync(
            new MiHoldingListQuery(groupId, MiHoldingList.Stalled, StalledCounted, now.AddDays(-options.MiStalledAfterDays)),
            cancellationToken);
        var markers = await MiReadModel.MarkersAsync(imports, groupId, period, refresh?.HistoryFromUtc, cancellationToken);

        var byStatus = buckets.Select((bucket, index) => new MiStatePointDto(
            bucket.Start,
            bucket.End,
            states.ToDictionary(state => state, state => atEnds[index].GetValueOrDefault((MiNames.Active, state))))).ToList();

        var activeInactive = buckets.Select((bucket, index) =>
        {
            var inBucket = events.Where(count => count.Day is { } day && bucket.Contains(day)).ToList();
            return new MiActivityPointDto(
                bucket.Start,
                bucket.End,
                atEnds[index].Where(entry => entry.Key.Status == MiNames.Active).Sum(entry => entry.Value),
                atEnds[index].Where(entry => entry.Key.Status == MiNames.Inactive).Sum(entry => entry.Value),
                inBucket.Where(count => IsRaised(unit, count)).Sum(count => count.Count),
                inBucket.Where(count => IsCameBack(unit, count)).Sum(count => count.Count),
                inBucket.Where(count => IsNoLongerFound(unit, count)).Sum(count => count.Count));
        }).ToList();

        var openOverTime = atEnds.Select(Open).ToList();
        var openAtStart = Open(atStart);
        var openAtEnd = openOverTime.Count == 0 ? openAtStart : openOverTime[^1];

        var unassigned = holdingCounts
            .Where(count => count.Status is HoldingStatus.Todo or HoldingStatus.InProgress && count.AssignedUserId is null)
            .Sum(count => count.Count);

        return new MiBacklogDto(
            groupId,
            MiReadModel.Status(groupId, refresh, now, options),
            MiReadModel.ToDto(period, historyFrom),
            unitName,
            ruleId,
            MiReadModel.ToDto(buckets),
            states,
            byStatus,
            activeInactive,
            openOverTime,
            openAtStart,
            openAtEnd,
            Waterfall(openAtStart, openAtEnd, events),
            minimumAge,
            unit == MiUnit.Issue ? IssueAges(issueCounts, today, minimumAge) : HoldingAges(holdingCounts, today, minimumAge),
            unit == MiUnit.Issue ? OpenByRule(issueCounts) : [],
            [.. oldest.Select(ToRow)],
            new MiStalledDto(options.MiStalledAfterDays, stalled.Count, [.. stalled.Take(StalledShown).Select(ToRow)]),
            unassigned,
            markers);
    }

    private static int Open(IReadOnlyDictionary<(string Status, string State), int> totals) =>
        totals.GetValueOrDefault((MiNames.Active, StatusNames.Todo)) +
        totals.GetValueOrDefault((MiNames.Active, StatusNames.InProgress));

    /// <summary>
    /// The number in each state at the start of the period, and at the end of each point: the
    /// running total of every day's net change.
    /// </summary>
    private static (Dictionary<(string Status, string State), int> AtStart, List<Dictionary<(string Status, string State), int>> AtEnds)
        RunningTotals(IReadOnlyList<MiDailyChange> daily, DateOnly from, IReadOnlyList<MiBucket> buckets)
    {
        var days = daily.OrderBy(change => change.Day).ToList();
        var running = new Dictionary<(string Status, string State), int>();
        var next = 0;

        void ApplyThrough(DateOnly last)
        {
            for (; next < days.Count && days[next].Day <= last; next++)
            {
                var key = (days[next].Status, days[next].State);
                running[key] = running.GetValueOrDefault(key) + days[next].Delta;
            }
        }

        ApplyThrough(from.AddDays(-1));
        var atStart = new Dictionary<(string Status, string State), int>(running);

        var atEnds = new List<Dictionary<(string Status, string State), int>>(buckets.Count);
        foreach (var bucket in buckets)
        {
            ApplyThrough(bucket.End);
            atEnds.Add(new Dictionary<(string Status, string State), int>(running));
        }

        return (atStart, atEnds);
    }

    private static bool IsRaised(MiUnit unit, MiEventCount count) => unit == MiUnit.Issue
        ? count.Kind == MiEventKind.Raised
        : count.Kind == MiEventKind.HoldingStatus && count.FromStatus is null && count.ToStatus == MiNames.Active;

    private static bool IsCameBack(MiUnit unit, MiEventCount count) => unit == MiUnit.Issue
        ? count.Kind == MiEventKind.CameBack
        : count.Kind == MiEventKind.HoldingStatus && count.FromStatus == MiNames.Inactive && count.ToStatus == MiNames.Active;

    private static bool IsNoLongerFound(MiUnit unit, MiEventCount count) => unit == MiUnit.Issue
        ? count.Kind == MiEventKind.NoLongerFound
        : count.Kind == MiEventKind.HoldingStatus && count.FromStatus == MiNames.Active && count.ToStatus == MiNames.Inactive;

    private static MiWaterfallDto Waterfall(int start, int end, IReadOnlyList<MiEventCount> events)
    {
        var steps = WaterfallOrder
            .Select(reason =>
            {
                var matching = events.Where(count => count.OpenChange == reason).ToList();
                return new MiWaterfallStepDto(
                    reason.ToWire(),
                    matching.Where(count => count.OpenDelta > 0).Sum(count => count.Count),
                    matching.Where(count => count.OpenDelta < 0).Sum(count => count.Count));
            })
            .Where(step => step.Reason != MiOpenChange.Correction.ToWire() || step.Up + step.Down > 0)
            .ToList();

        return new MiWaterfallDto(start, end, steps);
    }

    private static IReadOnlyList<MiAgeBandDto> IssueAges(
        IReadOnlyList<MiIssueStateCount> counts, DateOnly today, int minimumAge) =>
        Ages(counts
            .Where(count => count.Status == IssueStatus.Active &&
                            count.WorkflowStatus is WorkflowStatus.Todo or WorkflowStatus.InProgress)
            .Select(count => (
                Age: MiCalendar.DaysBetween(count.FirstRaisedDay, today),
                InProgress: count.WorkflowStatus == WorkflowStatus.InProgress,
                count.Count)), minimumAge);

    private static IReadOnlyList<MiAgeBandDto> HoldingAges(
        IReadOnlyList<MiHoldingStateCount> counts, DateOnly today, int minimumAge) =>
        Ages(counts
            .Where(count => count.Status is HoldingStatus.Todo or HoldingStatus.InProgress && count.OldestOpenRaisedDay is not null)
            .Select(count => (
                Age: MiCalendar.DaysBetween(count.OldestOpenRaisedDay!.Value, today),
                InProgress: count.Status == HoldingStatus.InProgress,
                count.Count)), minimumAge);

    private static IReadOnlyList<MiAgeBandDto> Ages(
        IEnumerable<(int Age, bool InProgress, int Count)> open, int minimumAge)
    {
        var all = open.ToList();
        return
        [
            .. MiCalendar.AgeBands.Select(band =>
            {
                var inBand = all.Where(entry => band.Contains(Math.Max(0, entry.Age))).ToList();
                return new MiAgeBandDto(
                    band.Id,
                    inBand.Where(entry => !entry.InProgress).Sum(entry => entry.Count),
                    inBand.Where(entry => entry.InProgress).Sum(entry => entry.Count),
                    inBand.Where(entry => entry.Age < minimumAge).Sum(entry => entry.Count));
            })
        ];
    }

    private static IReadOnlyList<MiRuleOpenDto> OpenByRule(IReadOnlyList<MiIssueStateCount> counts) =>
    [
        .. counts
            .Where(count => count.Status == IssueStatus.Active)
            .GroupBy(count => count.RuleId, StringComparer.Ordinal)
            .Select(group => new MiRuleOpenDto(
                group.Key,
                group.Where(count => count.WorkflowStatus == WorkflowStatus.Todo).Sum(count => count.Count),
                group.Where(count => count.WorkflowStatus == WorkflowStatus.InProgress).Sum(count => count.Count)))
            .Where(rule => rule.Todo + rule.InProgress > 0)
            .OrderBy(rule => rule.RuleId, StringComparer.Ordinal)
    ];

    private static MiHoldingRowDto ToRow(MiHoldingState holding) => new(
        holding.Cph,
        holding.Status.ToWire(),
        holding.OpenIssues,
        holding.OldestOpenRaisedAtUtc,
        holding.AssignedUserId,
        holding.AssigneeName,
        holding.LastWorkedAtUtc,
        holding.InProgressSinceUtc);
}
