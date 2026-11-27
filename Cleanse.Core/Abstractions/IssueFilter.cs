using Cleanse.Core.Domain;

namespace Cleanse.Core.Abstractions;

public enum IssueSortField
{
    UpdatedAtUtc,
    CreatedAtUtc,
    Id,
    RuleId,
    WorkflowStatus
}

public enum SortDirection
{
    Ascending,
    Descending
}

/// <summary>What a grouped search orders its groups by.</summary>
public enum IssueGroupSortField
{
    LatestUpdatedAtUtc,
    IssueCount
}

/// <summary>
/// A declarative filter rather than an <c>IQueryable</c>, so that a document store, Postgres
/// and SQLite can each honour the same query surface without the Core leaking expression trees.
/// </summary>
public sealed record IssueFilter
{
    public string? GroupId { get; init; }

    public string? RuleId { get; init; }

    public string? RunId { get; init; }

    public IssueStatus? Status { get; init; }

    public IReadOnlyCollection<WorkflowStatus>? WorkflowStatuses { get; init; }

    /// <summary>The assignee filters match the issue's holding's assignee, not the issue's own.</summary>
    public string? AssignedTo { get; init; }

    public Guid? AssignedUserId { get; init; }

    /// <summary>Issues whose holding has neither a known user nor a nonblank assignee.</summary>
    public bool? Unassigned { get; init; }

    /// <summary>All pairs must match for an issue to be returned.</summary>
    public IReadOnlyDictionary<string, string>? ContextMatches { get; init; }

    public DateTimeOffset? CreatedAfterUtc { get; init; }

    public DateTimeOffset? CreatedBeforeUtc { get; init; }

    public DateTimeOffset? UpdatedAfterUtc { get; init; }

    public DateTimeOffset? UpdatedBeforeUtc { get; init; }

    /// <summary>Leaves out issues that have not yet reached this age, and those the rules closed before they did.</summary>
    public IssueAgeThreshold? MinimumAge { get; init; }

    /// <summary>Case-insensitive match across the id, notes and context values.</summary>
    public string? SearchTerm { get; init; }

    public IssueSortField SortBy { get; init; } = IssueSortField.UpdatedAtUtc;

    public SortDirection SortDirection { get; init; } = SortDirection.Descending;

    /// <summary>Orders the groups of a grouped search; ignored by a flat search.</summary>
    public IssueGroupSortField GroupSortBy { get; init; } = IssueGroupSortField.LatestUpdatedAtUtc;

    public SortDirection GroupSortDirection { get; init; } = SortDirection.Descending;

    public PageRequest Page { get; init; } = PageRequest.Default;
}

/// <summary>
/// An issue matches when it was raised at least <see cref="Age"/> before <see cref="AsOfUtc"/>
/// and, if the rules have closed it, was still open when it reached that age.
/// </summary>
public sealed record IssueAgeThreshold(TimeSpan Age, DateTimeOffset AsOfUtc)
{
    /// <summary>The latest an issue can have been raised and still match.</summary>
    public DateTimeOffset RaisedByUtc => (AsOfUtc - Age).ToUniversalTime();
}

public sealed record IssueStatusCount(IssueStatus Status, WorkflowStatus WorkflowStatus, int Count);

/// <summary>
/// The matching issues that share one context value. Issues without the key share the empty
/// key. <see cref="Issues"/> holds every issue in the grouping, ordered by the filter's sort.
/// </summary>
public sealed record IssueGrouping(
    string Key,
    int IssueCount,
    DateTimeOffset LatestUpdatedAtUtc,
    IReadOnlyList<Issue> Issues);
