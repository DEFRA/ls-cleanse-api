using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Mi;

namespace Cleanse.Storage.InMemory;

/// <summary>
/// The dashboard's summary, one immutable set per group, swapped whole so a reader never sees half
/// of one. Built from the other in-memory stores, read under their own locks.
/// </summary>
public sealed class InMemoryMiStore(
    InMemoryIssueStore issues,
    InMemoryAuditStore audit,
    InMemoryHoldingStore holdings,
    InMemoryHoldingActivityStore activity,
    InMemoryRuleRunStore runs) : IMiStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, MiSummary> _summaries = new(StringComparer.Ordinal);

    public void Clear(string? groupId)
    {
        lock (_gate)
        {
            if (groupId is null)
            {
                _summaries.Clear();
            }
            else
            {
                _summaries.Remove(groupId);
            }
        }
    }

    public Task<string> GetFingerprintAsync(string groupId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Fingerprint(groupId));
    }

    public Task<MiHistory> ReadHistoryAsync(string groupId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        cancellationToken.ThrowIfCancellationRequested();

        var fingerprint = Fingerprint(groupId);
        var rows = issues.Snapshot(groupId);
        var ids = rows.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);

        var facts = audit.Snapshot()
            .Where(x => x.Entry.IssueId is { } id && ids.Contains(id.Value))
            .Select(x => MiAuditFact.From(x.Entry, x.Order))
            .OfType<MiAuditFact>()
            .ToList();

        var (notes, actions) = activity.Snapshot(groupId);
        IReadOnlyList<MiHistoryActivity> recorded =
        [
            .. actions.Select(x => new MiHistoryActivity(
                MiUnit.Action, x.Action.Cph, x.Action.CreatedAtUtc, x.Order, x.Action.CreatedBy,
                x.Action.ActionTypeId, x.Action.ActionTypeName)),
            .. notes.Select(x => new MiHistoryActivity(
                MiUnit.Note, x.Note.Cph, x.Note.CreatedAtUtc, x.Order, x.Note.CreatedBy))
        ];

        return Task.FromResult(new MiHistory(
            groupId,
            fingerprint,
            [.. rows.Select(row => new MiHistoryIssue(
                row.Id,
                row.RuleId,
                row.Context.TryGetValue(HoldingKey.CphContextKey, out var cph) && !string.IsNullOrWhiteSpace(cph) ? cph : null,
                row.Status,
                row.WorkflowStatus,
                row.OutcomeReasonId,
                row.OutcomeReasonName,
                row.CreatedAtUtc,
                row.DeactivatedAtUtc,
                row.UpdatedAtUtc))],
            facts,
            [.. holdings.Snapshot(groupId).Select(h => new MiHistoryHolding(
                h.Cph, h.AssignedUserId, h.AssignedTo, h.AssignedUserFullName))],
            recorded,
            runs.FirstStartedAt(groupId)));
    }

    public Task<bool> ReplaceAsync(MiSummary summary, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _summaries[summary.Refresh.GroupId] = summary;
        }

        return Task.FromResult(true);
    }

    public Task<MiRefreshRecord?> GetRefreshAsync(string groupId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Summary(groupId)?.Refresh);
    }

    public Task MarkCheckedAsync(string groupId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_summaries.TryGetValue(groupId, out var summary))
            {
                _summaries[groupId] = summary with { Refresh = summary.Refresh with { CheckedAtUtc = at } };
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MiEventCount>> CountEventsAsync(
        MiEventFilter filter,
        MiEventDimensions by,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<MiEventCount> counts =
        [
            .. (Summary(filter.GroupId)?.Events ?? [])
                .Where(filter.Matches)
                .GroupBy(e => MiEventCount.KeyOf(e, by))
                .Select(group => group.Key with { Count = group.Count(), LastAtUtc = group.Max(e => e.AtUtc) })
        ];

        return Task.FromResult(counts);
    }

    public Task<IReadOnlyList<MiDailyChange>> GetDailyChangesAsync(
        string groupId,
        MiUnit unit,
        string? ruleId,
        DateOnly toDay,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<MiDailyChange> changes =
        [
            .. (Summary(groupId)?.DailyChanges ?? [])
                .Where(change => change.Unit == unit &&
                                 change.Day <= toDay &&
                                 (ruleId is null || string.Equals(change.RuleId, ruleId, StringComparison.Ordinal)))
        ];

        return Task.FromResult(changes);
    }

    public Task<IReadOnlyList<MiIssueStateCount>> CountIssueStatesAsync(
        string groupId,
        string? ruleId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<MiIssueStateCount> counts =
        [
            .. (Summary(groupId)?.Issues ?? [])
                .Where(issue => ruleId is null || string.Equals(issue.RuleId, ruleId, StringComparison.Ordinal))
                .GroupBy(issue => (issue.RuleId, issue.Status, issue.WorkflowStatus, issue.FirstRaisedDay))
                .Select(group => new MiIssueStateCount(
                    group.Key.RuleId, group.Key.Status, group.Key.WorkflowStatus, group.Key.FirstRaisedDay, group.Count()))
        ];

        return Task.FromResult(counts);
    }

    public Task<IReadOnlyList<MiHoldingStateCount>> CountHoldingStatesAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<MiHoldingStateCount> counts =
        [
            .. (Summary(groupId)?.Holdings ?? [])
                .GroupBy(holding => (holding.Status, holding.HasActiveIssue, holding.AssignedUserId, holding.OldestOpenRaisedDay))
                .Select(group => new MiHoldingStateCount(
                    group.Key.Status, group.Key.HasActiveIssue, group.Key.AssignedUserId, group.Key.OldestOpenRaisedDay,
                    group.Count()))
        ];

        return Task.FromResult(counts);
    }

    public Task<IReadOnlyList<MiHoldingState>> ListHoldingStatesAsync(
        MiHoldingListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var all = Summary(query.GroupId)?.Holdings ?? [];
        IEnumerable<MiHoldingState> listed = query.List switch
        {
            MiHoldingList.Stalled => all
                .Where(holding => IsStalled(holding, query.Before))
                .OrderBy(holding => holding.LastWorkedAtUtc ?? holding.InProgressSinceUtc)
                .ThenBy(holding => holding.Cph, StringComparer.Ordinal),
            _ => all
                .Where(holding => holding.IsOpen && holding.OldestOpenRaisedAtUtc is not null)
                .OrderBy(holding => holding.OldestOpenRaisedAtUtc)
                .ThenBy(holding => holding.Cph, StringComparer.Ordinal)
        };

        IReadOnlyList<MiHoldingState> found = [.. listed.Take(query.Limit)];
        return Task.FromResult(found);
    }

    /// <summary>In progress since before the cut-off, and not worked on since it either.</summary>
    internal static bool IsStalled(MiHoldingState holding, DateTimeOffset? before) =>
        holding.Status == HoldingStatus.InProgress &&
        before is { } cutOff &&
        holding.InProgressSinceUtc is { } since && since <= cutOff &&
        (holding.LastWorkedAtUtc is null || holding.LastWorkedAtUtc <= cutOff);

    private MiSummary? Summary(string groupId)
    {
        lock (_gate)
        {
            return _summaries.GetValueOrDefault(groupId);
        }
    }

    private string Fingerprint(string groupId)
    {
        var rows = issues.Snapshot(groupId);
        var (notes, actions) = activity.Snapshot(groupId);
        var stored = holdings.Snapshot(groupId);
        return string.Join(
            '|',
            rows.Count,
            rows.Count == 0 ? string.Empty : rows.Max(row => row.UpdatedAtUtc).ToString("O"),
            audit.Count,
            notes.Count,
            actions.Count,
            stored.Count,
            stored.Count == 0 ? string.Empty : stored.Max(holding => holding.UpdatedAtUtc).ToString("O"));
    }
}
