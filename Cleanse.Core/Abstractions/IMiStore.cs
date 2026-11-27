using Cleanse.Core.Domain.Mi;

namespace Cleanse.Core.Abstractions;

/// <summary>
/// The dashboard's summary: built from the issues and their audit trail, then read by the
/// dashboard instead of the history. Core works the figures out; a store only reads its source
/// in one snapshot, swaps a whole summary in, and counts what is in it.
/// </summary>
public interface IMiStore
{
    /// <summary>
    /// Changes whenever anything the summary is built from changes. Cheap, so it can be asked
    /// often to decide whether a refresh has anything to do.
    /// </summary>
    Task<string> GetFingerprintAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>Everything a summary is built from, read consistently, with its fingerprint.</summary>
    Task<MiHistory> ReadHistoryAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the group's summary in one transaction, so readers see the old one until the new
    /// one is complete. Returns false, writing nothing, while another writer is replacing it.
    /// </summary>
    Task<bool> ReplaceAsync(MiSummary summary, CancellationToken cancellationToken = default);

    /// <summary>When the summary was last built, or null if it never has been.</summary>
    Task<MiRefreshRecord?> GetRefreshAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>Records that a refresh found nothing new at <paramref name="at"/>.</summary>
    Task MarkCheckedAsync(string groupId, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>How many of the matching events share each combination of the dimensions asked for.</summary>
    Task<IReadOnlyList<MiEventCount>> CountEventsAsync(
        MiEventFilter filter,
        MiEventDimensions by,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Each day's net changes up to <paramref name="toDay"/>, for issues or for holdings, in one
    /// rule when <paramref name="ruleId"/> is given.
    /// </summary>
    Task<IReadOnlyList<MiDailyChange>> GetDailyChangesAsync(
        string groupId,
        MiUnit unit,
        string? ruleId,
        DateOnly toDay,
        CancellationToken cancellationToken = default);

    /// <summary>The issues now, by rule, status, workflow status and the day they were first raised.</summary>
    Task<IReadOnlyList<MiIssueStateCount>> CountIssueStatesAsync(
        string groupId,
        string? ruleId,
        CancellationToken cancellationToken = default);

    /// <summary>The holdings now, by status, whether they have an active issue, assignee and age.</summary>
    Task<IReadOnlyList<MiHoldingStateCount>> CountHoldingStatesAsync(
        string groupId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MiHoldingState>> ListHoldingStatesAsync(
        MiHoldingListQuery query,
        CancellationToken cancellationToken = default);
}
