using System.Globalization;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Queries.Holdings;

/// <summary>
/// Everything the holding page shows: every issue at the CPH, the holding's status and assignee,
/// its notes and its history. Notes and history include what was recorded against single issues
/// before work moved to the holding, labelled with the issue.
/// </summary>
public sealed record GetHoldingQuery(HoldingKey Holding) : IQuery<HoldingDetailDto>;

internal sealed class GetHoldingQueryHandler(
    IIssueStore issues,
    IHoldingStore holdings,
    IHoldingActivityStore activity,
    IIssueHistoryStore history,
    IAuditStore audit) : IQueryHandler<GetHoldingQuery, HoldingDetailDto>
{
    /// <summary>How much of each kind of record the page shows. A holding rarely has more.</summary>
    internal const int Limit = 200;

    public async Task<Result<HoldingDetailDto>> HandleAsync(
        GetHoldingQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var key = query.Holding;
        var all = await issues.GetForHoldingsAsync(key.GroupId, [key.Cph], cancellationToken);
        if (all.Count == 0)
        {
            return DomainErrors.HoldingNotFound(key.GroupId, key.Cph);
        }

        var holding = await holdings.GetAsync(key, cancellationToken);
        var ordered = all
            .OrderBy(Rank)
            .ThenBy(RuleOrder)
            .ThenBy(issue => issue.Id.Value, StringComparer.Ordinal)
            .ToList();

        var notes = (await activity.GetNotesAsync(key, Limit, cancellationToken))
            .Select(note => note.ToDto())
            .ToList();
        var events = new List<HoldingEventDto>();

        foreach (var batch in (await activity.GetActionsAsync(key, Limit, cancellationToken)).GroupBy(action => action.BatchId))
        {
            var first = batch.First();
            events.Add(Event(
                HoldingEventKinds.ActionsRecorded,
                first.CreatedAtUtc,
                first.CreatedBy,
                actions: [.. batch.Select(action => action.ActionTypeName).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)]));
        }

        var page = new PageRequest(1, Limit);
        foreach (var entry in (await audit.GetForSubjectAsync(AuditSubjects.Holding(key), page, cancellationToken)).Items)
        {
            if (entry.Kind == AuditEventKind.HoldingAssigned)
            {
                events.Add(Event(
                    HoldingEventKinds.AssignmentChanged,
                    entry.AtUtc,
                    entry.Actor,
                    assignedTo: Change(entry, nameof(Holding.AssignedUserFullName))?.To ??
                                Change(entry, nameof(Holding.AssignedTo))?.To));
            }
        }

        foreach (var issue in ordered)
        {
            foreach (var entry in (await audit.GetForIssueAsync(issue.Id, page, cancellationToken)).Items)
            {
                if (StatusEvent(issue, entry) is { } changed)
                {
                    events.Add(changed);
                }
            }

            foreach (var entry in (await history.GetForIssueAsync(issue.Id, page, cancellationToken)).Items)
            {
                if (entry.ActionType == HistoryActionType.Comment)
                {
                    notes.Add(new HoldingNoteDto(
                        entry.Id, entry.Comments, null, issue.Id.Value, entry.CreatedAtUtc,
                        entry.CreatedBy.Id, entry.CreatedBy.DisplayName));
                }
                else
                {
                    events.Add(Event(
                        HoldingEventKinds.Contact,
                        entry.CreatedAtUtc,
                        entry.CreatedBy,
                        issueId: issue.Id.Value,
                        actions: [entry.ActionType.ToString()],
                        detail: entry.Comments));
                }
            }
        }

        return new HoldingDetailDto(
            key.GroupId,
            key.Cph,
            HoldingStatuses.Derive(all).ToWire(),
            HoldingStatuses.SuppressedUntil(all),
            holding?.AssignedTo,
            holding?.AssignedUserId,
            holding?.AssignedUserFullName,
            holding?.AssignmentDetachedAtUtc,
            holding?.Version ?? 0,
            [.. ordered.Select(issue => issue.ToDto(holding))],
            // Stable sorts: entries sharing an instant keep the stores' newest-first order.
            [.. notes.OrderByDescending(note => note.CreatedAtUtc).Take(Limit)],
            [.. events.OrderByDescending(e => e.AtUtc).Take(Limit)]);
    }

    /// <summary>Open issues first, then suppressed, then those with an outcome, then the inactive ones.</summary>
    private static int Rank(Issue issue) => issue switch
    {
        { Status: IssueStatus.Inactive } => 3,
        _ when issue.IsOpen() => 0,
        { WorkflowStatus: WorkflowStatus.Suppressed } => 1,
        _ => 2
    };

    private static int RuleOrder(Issue issue) =>
        issue.Context.TryGetValue("ruleNumber", out var number) &&
        int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var rule)
            ? rule
            : int.MaxValue;

    private static HoldingEventDto? StatusEvent(Issue issue, AuditEntry entry)
    {
        var status = Change(entry, nameof(Issue.WorkflowStatus));
        var reason = Change(entry, "OutcomeReason");
        var expiry = Change(entry, nameof(Issue.SuppressionExpiresAtUtc));

        var relevant = entry.Kind switch
        {
            AuditEventKind.IssueWorkflowStatusChanged => status is not null || reason is not null,
            AuditEventKind.IssueSuppressed => true,
            AuditEventKind.IssueSuppressionExpired => true,
            AuditEventKind.IssueExternalStateApplied => status is not null,
            _ => false
        };

        if (!relevant)
        {
            return null;
        }

        // A suppression that only moved its end date names no status change, but is still one.
        var now = status?.To ?? (entry.Kind == AuditEventKind.IssueSuppressed ? StatusNames.Suppressed : issue.WorkflowStatus.ToWire());
        DateTimeOffset? until = entry.Kind == AuditEventKind.IssueSuppressed &&
                                DateTimeOffset.TryParse(expiry?.To, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

        return Event(
            HoldingEventKinds.StatusChanged,
            entry.AtUtc,
            entry.Actor,
            issueId: issue.Id.Value,
            workflowStatus: now,
            previousWorkflowStatus: status?.From ?? now,
            outcomeReason: reason?.To,
            suppressedUntilUtc: until);
    }

    private static AuditChange? Change(AuditEntry entry, string field) =>
        entry.Changes.FirstOrDefault(change => string.Equals(change.Field, field, StringComparison.Ordinal));

    private static HoldingEventDto Event(
        string kind,
        DateTimeOffset at,
        Actor actor,
        string? issueId = null,
        string? workflowStatus = null,
        string? previousWorkflowStatus = null,
        string? outcomeReason = null,
        DateTimeOffset? suppressedUntilUtc = null,
        string? assignedTo = null,
        IReadOnlyList<string>? actions = null,
        string? detail = null) =>
        new(kind, at, actor.Id, actor.DisplayName, issueId, workflowStatus, previousWorkflowStatus,
            outcomeReason, suppressedUntilUtc, assignedTo, actions ?? [], detail);
}
