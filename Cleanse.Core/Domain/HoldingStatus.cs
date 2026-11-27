namespace Cleanse.Core.Domain;

/// <summary>
/// A holding's status, worked out from its issues whenever it is read and never stored, so
/// nothing has to be kept in step when the rules raise or deactivate an issue.
/// </summary>
public enum HoldingStatus
{
    /// <summary>An active issue is To do, and none is In progress.</summary>
    Todo,

    /// <summary>An active issue is In progress.</summary>
    InProgress,

    /// <summary>No active issue is open, and at least one is suppressed.</summary>
    Suppressed,

    /// <summary>No active issue is open or suppressed. Inactive issues never count.</summary>
    Closed
}

public static class HoldingStatuses
{
    public const string Todo = StatusNames.Todo;
    public const string InProgress = StatusNames.InProgress;
    public const string Suppressed = StatusNames.Suppressed;
    public const string Closed = "closed";

    public static HoldingStatus Derive(IEnumerable<Issue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);

        var todo = false;
        var suppressed = false;
        foreach (var issue in issues)
        {
            if (issue.Status != IssueStatus.Active)
            {
                continue;
            }

            switch (issue.WorkflowStatus)
            {
                case WorkflowStatus.InProgress:
                    return HoldingStatus.InProgress;
                case WorkflowStatus.Todo:
                    todo = true;
                    break;
                case WorkflowStatus.Suppressed:
                    suppressed = true;
                    break;
            }
        }

        return todo ? HoldingStatus.Todo : suppressed ? HoldingStatus.Suppressed : HoldingStatus.Closed;
    }

    /// <summary>When the first of the holding's active suppressions ends, if any.</summary>
    public static DateTimeOffset? SuppressedUntil(IEnumerable<Issue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        return issues
            .Where(i => i.Status == IssueStatus.Active && i.WorkflowStatus == WorkflowStatus.Suppressed)
            .Select(i => i.SuppressionExpiresAtUtc)
            .Min();
    }

    /// <summary>Open is what the triage queue works through: active, and To do or In progress.</summary>
    public static bool IsOpen(this Issue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return issue.Status == IssueStatus.Active &&
               issue.WorkflowStatus is WorkflowStatus.Todo or WorkflowStatus.InProgress;
    }

    public static string ToWire(this HoldingStatus status) => status switch
    {
        HoldingStatus.Todo => Todo,
        HoldingStatus.InProgress => InProgress,
        HoldingStatus.Suppressed => Suppressed,
        HoldingStatus.Closed => Closed,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };
}
