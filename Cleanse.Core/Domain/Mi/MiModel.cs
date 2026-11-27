using System.Globalization;

namespace Cleanse.Core.Domain.Mi;

/// <summary>What a summary row counts.</summary>
public enum MiUnit
{
    Issue,
    Holding,
    Action,
    Note
}

public enum MiEventKind
{
    /// <summary>The rules found the issue for the first time.</summary>
    Raised,

    /// <summary>The rules found an inactive issue again.</summary>
    CameBack,

    /// <summary>The rules stopped finding an active issue, so the end-of-run sweep made it inactive.</summary>
    NoLongerFound,

    /// <summary>The issue's workflow status changed.</summary>
    Workflow,

    /// <summary>The issue's history did not lead to its current state, so the summary was put right.</summary>
    Corrected,

    /// <summary>A holding's status, or whether it has an active issue, changed.</summary>
    HoldingStatus,

    Action,

    Note
}

/// <summary>Who or what made a change. Only <see cref="Cleanse"/> is anyone's work.</summary>
public enum MiSource
{
    Rules,
    Cleanse,
    Spol,
    Expiry,
    Correction
}

/// <summary>Why the number open went up or down.</summary>
public enum MiOpenChange
{
    Raised,
    CameBack,
    Reopened,
    SuppressionEnded,
    Spol,
    Resolved,
    CantResolve,
    Suppressed,
    NoLongerFound,
    Correction
}

public enum MiInterval
{
    Day,
    Week,
    Month
}

/// <summary>The wire and column names, which are what the summary tables store.</summary>
public static class MiNames
{
    public const string Active = StatusNames.Active;
    public const string Inactive = StatusNames.Inactive;

    public static string ToWire(this MiUnit unit) => unit switch
    {
        MiUnit.Issue => "issue",
        MiUnit.Holding => "holding",
        MiUnit.Action => "action",
        MiUnit.Note => "note",
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null)
    };

    public static string ToWire(this MiEventKind kind) => kind switch
    {
        MiEventKind.Raised => "raised",
        MiEventKind.CameBack => "came-back",
        MiEventKind.NoLongerFound => "no-longer-found",
        MiEventKind.Workflow => "workflow",
        MiEventKind.Corrected => "corrected",
        MiEventKind.HoldingStatus => "holding-status",
        MiEventKind.Action => "action",
        MiEventKind.Note => "note",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public static string ToWire(this MiSource source) => source switch
    {
        MiSource.Rules => "rules",
        MiSource.Cleanse => "cleanse",
        MiSource.Spol => "spol",
        MiSource.Expiry => "expiry",
        MiSource.Correction => "correction",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null)
    };

    public static string ToWire(this MiOpenChange change) => change switch
    {
        MiOpenChange.Raised => "raised",
        MiOpenChange.CameBack => "came-back",
        MiOpenChange.Reopened => "reopened",
        MiOpenChange.SuppressionEnded => "suppression-ended",
        MiOpenChange.Spol => "spol",
        MiOpenChange.Resolved => "resolved",
        MiOpenChange.CantResolve => "cant-resolve",
        MiOpenChange.Suppressed => "suppressed",
        MiOpenChange.NoLongerFound => "no-longer-found",
        MiOpenChange.Correction => "correction",
        _ => throw new ArgumentOutOfRangeException(nameof(change), change, null)
    };

    public static string ToWire(this MiInterval interval) => interval switch
    {
        MiInterval.Day => "day",
        MiInterval.Week => "week",
        MiInterval.Month => "month",
        _ => throw new ArgumentOutOfRangeException(nameof(interval), interval, null)
    };

    public static MiUnit ParseUnit(string value) => Parse<MiUnit>(value, ToWire);

    public static MiEventKind ParseKind(string value) => Parse<MiEventKind>(value, ToWire);

    public static MiSource ParseSource(string value) => Parse<MiSource>(value, ToWire);

    public static MiOpenChange ParseOpenChange(string value) => Parse<MiOpenChange>(value, ToWire);

    public static bool TryParseInterval(string? value, out MiInterval interval)
    {
        foreach (var candidate in Enum.GetValues<MiInterval>())
        {
            if (string.Equals(candidate.ToWire(), value?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                interval = candidate;
                return true;
            }
        }

        interval = default;
        return false;
    }

    public static string ToStatusWire(bool active) => active ? Active : Inactive;

    private static T Parse<T>(string value, Func<T, string> toWire) where T : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(toWire(candidate), value, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"'{value}' is not a known {typeof(T).Name}.");
    }
}

/// <summary>
/// One change the dashboard counts, as the summary holds it. Issue changes carry both statuses
/// before and after; a holding's carry whether it had an active issue as the status, and its
/// holding status as the workflow status. The person is set only for a known user's change in
/// Cleanse, and <see cref="InactiveNow"/> is as at the refresh that wrote the row.
/// </summary>
public sealed record MiEvent(
    string GroupId,
    long Sequence,
    DateTimeOffset AtUtc,
    DateOnly Day,
    MiUnit Unit,
    MiEventKind Kind,
    MiSource Source,
    string? IssueId,
    string? RuleId,
    string? Cph,
    Guid? UserId,
    string? UserName,
    string? FromStatus,
    string? ToStatus,
    string? FromWorkflow,
    string? ToWorkflow,
    Guid? ReasonId,
    string? ReasonName,
    MiOpenChange? OpenChange,
    int OpenDelta,
    int? ElapsedDays = null,
    bool? InactiveNow = null,
    int? ActionsSinceClosed = null);

/// <summary>An issue's state at the last refresh.</summary>
public sealed record MiIssueState(
    string GroupId,
    string IssueId,
    string RuleId,
    string? Cph,
    IssueStatus Status,
    WorkflowStatus WorkflowStatus,
    Guid? ReasonId,
    string? ReasonName,
    DateTimeOffset FirstRaisedAtUtc,
    DateOnly FirstRaisedDay,
    DateTimeOffset LastChangedAtUtc)
{
    public bool IsOpen => Status == IssueStatus.Active &&
                          WorkflowStatus is WorkflowStatus.Todo or WorkflowStatus.InProgress;
}

/// <summary>A holding's state at the last refresh, worked out from its issues as everywhere else.</summary>
public sealed record MiHoldingState(
    string GroupId,
    string Cph,
    HoldingStatus Status,
    bool HasActiveIssue,
    int OpenIssues,
    DateTimeOffset? OldestOpenRaisedAtUtc,
    DateOnly? OldestOpenRaisedDay,
    DateTimeOffset? InProgressSinceUtc,
    DateTimeOffset? LastWorkedAtUtc,
    Guid? AssignedUserId,
    string? AssigneeName)
{
    public bool IsOpen => Status is HoldingStatus.Todo or HoldingStatus.InProgress;
}

/// <summary>
/// The net change on one UK day in how many issues, or holdings, were in one state. For issues the
/// state is the workflow status, within a rule and a status; for holdings it is the holding status,
/// and the status says whether they had an active issue. The rule is empty for holdings.
/// </summary>
public sealed record MiDailyChange(
    string GroupId,
    DateOnly Day,
    MiUnit Unit,
    string RuleId,
    string Status,
    string State,
    int Delta);

/// <summary>
/// When the summary was last built, and from what. <see cref="CheckedAtUtc"/> is when a refresh
/// last found nothing new, so the figures were current then even if they were built long before.
/// </summary>
public sealed record MiRefreshRecord(
    string GroupId,
    DateTimeOffset BuiltAtUtc,
    DateTimeOffset CheckedAtUtc,
    string Fingerprint,
    int Version,
    int DurationMilliseconds,
    DateTimeOffset? HistoryFromUtc,
    int Issues,
    int Events,
    int Mismatches);

/// <summary>Everything one refresh writes for a group, replacing what was there.</summary>
public sealed record MiSummary(
    MiRefreshRecord Refresh,
    IReadOnlyList<MiIssueState> Issues,
    IReadOnlyList<MiHoldingState> Holdings,
    IReadOnlyList<MiEvent> Events,
    IReadOnlyList<MiDailyChange> DailyChanges)
{
    /// <summary>The issues counted in <see cref="MiRefreshRecord.Mismatches"/>, for the log. Not stored.</summary>
    public IReadOnlyList<string> MismatchedIssueIds { get; init; } = [];
}

/// <summary>What a summary is built from, read by the store in one consistent snapshot.</summary>
public sealed record MiHistory(
    string GroupId,
    string Fingerprint,
    IReadOnlyList<MiHistoryIssue> Issues,
    IReadOnlyList<MiAuditFact> AuditFacts,
    IReadOnlyList<MiHistoryHolding> Holdings,
    IReadOnlyList<MiHistoryActivity> Activity,
    DateTimeOffset? FirstRunStartedAtUtc);

public sealed record MiHistoryIssue(
    string Id,
    string RuleId,
    string? Cph,
    IssueStatus Status,
    WorkflowStatus WorkflowStatus,
    Guid? ReasonId,
    string? ReasonName,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DeactivatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record MiHistoryHolding(string Cph, Guid? AssignedUserId, string? AssignedTo, string? AssignedUserFullName);

/// <summary>An action or a note recorded on a holding. <see cref="Order"/> breaks ties on time.</summary>
public sealed record MiHistoryActivity(
    MiUnit Unit,
    string Cph,
    DateTimeOffset AtUtc,
    long Order,
    Actor CreatedBy,
    Guid? ActionTypeId = null,
    string? ActionTypeName = null);

/// <summary>
/// The part of one issue audit entry the summary needs. Kept small, because a refresh holds one
/// for every relevant entry in the group's history.
/// </summary>
public sealed record MiAuditFact(
    string IssueId,
    DateTimeOffset AtUtc,
    long Order,
    AuditEventKind Kind,
    Actor Actor,
    WorkflowStatus? WorkflowFrom,
    WorkflowStatus? WorkflowTo,
    bool ReasonChanged,
    Guid? ReasonId,
    string? ReasonName,
    DateTimeOffset? DeactivatedFrom)
{
    /// <summary>The audit entry kinds a refresh reads. Creation comes from the issue itself.</summary>
    public static readonly IReadOnlyList<AuditEventKind> Kinds =
    [
        AuditEventKind.IssueReactivated,
        AuditEventKind.IssueWorkflowStatusChanged,
        AuditEventKind.IssueSuppressed,
        AuditEventKind.IssueSuppressionExpired,
        AuditEventKind.IssueExternalStateApplied
    ];

    /// <summary>The fact in an entry, or null if the entry says nothing the summary counts.</summary>
    public static MiAuditFact? From(AuditEntry entry, long order)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IssueId is not { } issueId || !Kinds.Contains(entry.Kind))
        {
            return null;
        }

        WorkflowStatus? from = null, to = null;
        var reasonChanged = false;
        Guid? reasonId = null;
        string? reasonName = null;
        DateTimeOffset? deactivatedFrom = null;

        foreach (var change in entry.Changes)
        {
            switch (change.Field)
            {
                case nameof(Issue.WorkflowStatus):
                    from = ParseWorkflow(change.From);
                    to = ParseWorkflow(change.To);
                    break;
                case nameof(Issue.OutcomeReasonId):
                    reasonChanged = true;
                    reasonId = Guid.TryParse(change.To, out var id) ? id : null;
                    break;
                case "OutcomeReason":
                    reasonChanged = true;
                    reasonName = string.IsNullOrWhiteSpace(change.To) ? null : change.To;
                    break;
                case nameof(Issue.DeactivatedAtUtc):
                    deactivatedFrom = DateTimeOffset.TryParse(
                        change.From, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                        ? at
                        : null;
                    break;
            }
        }

        var counts = entry.Kind == AuditEventKind.IssueReactivated || to is not null || reasonChanged;
        return counts
            ? new MiAuditFact(issueId.Value, entry.AtUtc, order, entry.Kind, entry.Actor, from, to,
                reasonChanged, reasonId, reasonName, deactivatedFrom)
            : null;
    }

    /// <summary>Entries from before the merge say <c>ignored</c>, which is now Can't resolve.</summary>
    private static WorkflowStatus? ParseWorkflow(string? value) =>
        string.Equals(value, "ignored", StringComparison.OrdinalIgnoreCase)
            ? WorkflowStatus.Nonactionable
            : StatusNames.TryParseWorkflowStatus(value, out var status) ? status : null;
}

/// <summary>What the dashboard can group summary events by.</summary>
[Flags]
public enum MiEventDimensions
{
    None = 0,
    Day = 1,
    Unit = 1 << 1,
    Kind = 1 << 2,
    Source = 1 << 3,
    User = 1 << 4,
    Rule = 1 << 5,
    Cph = 1 << 6,
    FromStatus = 1 << 7,
    ToStatus = 1 << 8,
    FromWorkflow = 1 << 9,
    ToWorkflow = 1 << 10,
    Reason = 1 << 11,
    OpenChange = 1 << 12,
    OpenDelta = 1 << 13,
    ElapsedDays = 1 << 14,
    InactiveNow = 1 << 15,
    ActionsSinceClosed = 1 << 16
}

/// <summary>Which events to count. Every set filter must match.</summary>
public sealed record MiEventFilter(string GroupId, DateOnly FromDay, DateOnly ToDay)
{
    public IReadOnlyCollection<MiUnit>? Units { get; init; }

    public IReadOnlyCollection<MiEventKind>? Kinds { get; init; }

    public IReadOnlyCollection<MiSource>? Sources { get; init; }

    public string? RuleId { get; init; }

    public Guid? UserId { get; init; }

    public bool Matches(MiEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return string.Equals(e.GroupId, GroupId, StringComparison.Ordinal) &&
               e.Day >= FromDay && e.Day <= ToDay &&
               (Units is null || Units.Contains(e.Unit)) &&
               (Kinds is null || Kinds.Contains(e.Kind)) &&
               (Sources is null || Sources.Contains(e.Source)) &&
               (RuleId is null || string.Equals(e.RuleId, RuleId, StringComparison.Ordinal)) &&
               (UserId is null || e.UserId == UserId);
    }
}

/// <summary>
/// How many events share the dimensions asked for. A dimension not asked for is null, as is one
/// whose value is null, such as the person behind a change nobody was named for.
/// </summary>
public sealed record MiEventCount(int Count, DateTimeOffset LastAtUtc)
{
    public DateOnly? Day { get; init; }

    public MiUnit? Unit { get; init; }

    public MiEventKind? Kind { get; init; }

    public MiSource? Source { get; init; }

    public Guid? UserId { get; init; }

    public string? RuleId { get; init; }

    public string? Cph { get; init; }

    public string? FromStatus { get; init; }

    public string? ToStatus { get; init; }

    public string? FromWorkflow { get; init; }

    public string? ToWorkflow { get; init; }

    public Guid? ReasonId { get; init; }

    public string? ReasonName { get; init; }

    public MiOpenChange? OpenChange { get; init; }

    public int? OpenDelta { get; init; }

    public int? ElapsedDays { get; init; }

    public bool? InactiveNow { get; init; }

    public int? ActionsSinceClosed { get; init; }

    /// <summary>The event's values for the dimensions asked for, and null for the rest.</summary>
    public static MiEventCount KeyOf(MiEvent e, MiEventDimensions by) => new(0, default)
    {
        Day = by.HasFlag(MiEventDimensions.Day) ? e.Day : null,
        Unit = by.HasFlag(MiEventDimensions.Unit) ? e.Unit : null,
        Kind = by.HasFlag(MiEventDimensions.Kind) ? e.Kind : null,
        Source = by.HasFlag(MiEventDimensions.Source) ? e.Source : null,
        UserId = by.HasFlag(MiEventDimensions.User) ? e.UserId : null,
        RuleId = by.HasFlag(MiEventDimensions.Rule) ? e.RuleId : null,
        Cph = by.HasFlag(MiEventDimensions.Cph) ? e.Cph : null,
        FromStatus = by.HasFlag(MiEventDimensions.FromStatus) ? e.FromStatus : null,
        ToStatus = by.HasFlag(MiEventDimensions.ToStatus) ? e.ToStatus : null,
        FromWorkflow = by.HasFlag(MiEventDimensions.FromWorkflow) ? e.FromWorkflow : null,
        ToWorkflow = by.HasFlag(MiEventDimensions.ToWorkflow) ? e.ToWorkflow : null,
        ReasonId = by.HasFlag(MiEventDimensions.Reason) ? e.ReasonId : null,
        ReasonName = by.HasFlag(MiEventDimensions.Reason) ? e.ReasonName : null,
        OpenChange = by.HasFlag(MiEventDimensions.OpenChange) ? e.OpenChange : null,
        OpenDelta = by.HasFlag(MiEventDimensions.OpenDelta) ? e.OpenDelta : null,
        ElapsedDays = by.HasFlag(MiEventDimensions.ElapsedDays) ? e.ElapsedDays : null,
        InactiveNow = by.HasFlag(MiEventDimensions.InactiveNow) ? e.InactiveNow : null,
        ActionsSinceClosed = by.HasFlag(MiEventDimensions.ActionsSinceClosed) ? e.ActionsSinceClosed : null
    };
}

/// <summary>How many issues are in one state, and were first raised on one day.</summary>
public sealed record MiIssueStateCount(
    string RuleId,
    IssueStatus Status,
    WorkflowStatus WorkflowStatus,
    DateOnly FirstRaisedDay,
    int Count);

/// <summary>How many holdings are in one state, with one assignee, oldest open issue raised on one day.</summary>
public sealed record MiHoldingStateCount(
    HoldingStatus Status,
    bool HasActiveIssue,
    Guid? AssignedUserId,
    DateOnly? OldestOpenRaisedDay,
    int Count);

public enum MiHoldingList
{
    /// <summary>Open holdings, the longest open first.</summary>
    OldestOpen,

    /// <summary>In progress holdings nobody has worked on since a date, the longest untouched first.</summary>
    Stalled
}

public sealed record MiHoldingListQuery(string GroupId, MiHoldingList List, int Limit, DateTimeOffset? Before = null);
