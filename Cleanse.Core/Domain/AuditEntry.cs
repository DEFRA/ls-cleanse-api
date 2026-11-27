namespace Cleanse.Core.Domain;

public enum AuditEventKind
{
    None,
    IssueCreated,
    IssueReactivated,
    IssueDeactivated,
    IssueContextChanged,
    IssueAssigned,
    IssueWorkflowStatusChanged,
    IssuePriorityChanged,
    IssueExternalStateApplied,
    IssueSuppressed,
    IssueSuppressionExpired,
    IssueNotesChanged,
    IssueHistoryAdded,
    RunStarted,
    RunCompleted,
    RunAbandoned,
    RunCancellationRequested,
    RunCancelled,
    RunFailed,
    HoldingAssigned,
    CodedValueAdded,
    CodedValueActivated,
    CodedValueDeactivated,
    SettingChanged
}

/// <summary>A single field-level change: what it was, what it became.</summary>
public sealed record AuditChange(string Field, string? From, string? To);

/// <summary>
/// An append-only record of a material change. Run lifecycle events carry a null
/// <see cref="IssueId"/>; the end-of-run sweep records one of these for the whole run rather
/// than one per deactivated issue. A change to anything else, such as a holding's assignee,
/// names what it was about in <see cref="Subject"/>.
/// </summary>
public sealed record AuditEntry
{
    private AuditEntry(
        Guid id,
        IssueId? issueId,
        string? runId,
        AuditEventKind kind,
        Actor actor,
        DateTimeOffset atUtc,
        IReadOnlyList<AuditChange> changes,
        string? subject)
    {
        Id = id;
        IssueId = issueId;
        RunId = runId;
        Kind = kind;
        Actor = actor;
        AtUtc = atUtc;
        Changes = changes;
        Subject = subject;
    }

    public Guid Id { get; }

    public IssueId? IssueId { get; }

    public string? RunId { get; }

    public AuditEventKind Kind { get; }

    public Actor Actor { get; }

    public DateTimeOffset AtUtc { get; }

    public IReadOnlyList<AuditChange> Changes { get; }

    /// <summary>See <see cref="AuditSubjects"/>.</summary>
    public string? Subject { get; }

    public static AuditEntry ForIssue(
        IssueId issueId,
        IssueChange change,
        Actor actor,
        DateTimeOffset atUtc,
        string? runId = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(actor);
        return new AuditEntry(Guid.CreateVersion7(atUtc), issueId, runId, change.Kind, actor, atUtc, change.Changes, null);
    }

    public static AuditEntry ForHolding(
        HoldingKey holding,
        IssueChange change,
        Actor actor,
        DateTimeOffset atUtc,
        string? runId = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(actor);
        return new AuditEntry(
            Guid.CreateVersion7(atUtc), null, runId, change.Kind, actor, atUtc, change.Changes,
            AuditSubjects.Holding(holding));
    }

    public static AuditEntry ForCodedValue(Guid id, IssueChange change, Actor actor, DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(actor);
        return new AuditEntry(
            Guid.CreateVersion7(atUtc), null, null, change.Kind, actor, atUtc, change.Changes,
            AuditSubjects.CodedValue(id));
    }

    public static AuditEntry ForSetting(string name, IssueChange change, Actor actor, DateTimeOffset atUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(actor);
        return new AuditEntry(
            Guid.CreateVersion7(atUtc), null, null, change.Kind, actor, atUtc, change.Changes,
            AuditSubjects.Setting(name));
    }

    public static AuditEntry ForRun(
        string runId,
        AuditEventKind kind,
        Actor actor,
        DateTimeOffset atUtc,
        IReadOnlyList<AuditChange>? changes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(actor);
        return new AuditEntry(Guid.CreateVersion7(atUtc), null, runId, kind, actor, atUtc, changes ?? [], null);
    }

    public static AuditEntry Rehydrate(
        Guid id,
        IssueId? issueId,
        string? runId,
        AuditEventKind kind,
        Actor actor,
        DateTimeOffset atUtc,
        IReadOnlyList<AuditChange> changes,
        string? subject = null) =>
        new(id, issueId, runId, kind, actor, atUtc, changes, subject);
}

/// <summary>How an audit entry names a subject that is neither an issue nor a run.</summary>
public static class AuditSubjects
{
    public const string HoldingPrefix = "holding:";
    public const string CodedValuePrefix = "coded-value:";
    public const string SettingPrefix = "setting:";

    public static string Holding(HoldingKey holding) => $"{HoldingPrefix}{holding.GroupId}:{holding.Cph}";

    /// <summary>Every holding subject in a group starts with this, for a reset scoped to it.</summary>
    public static string HoldingsInGroup(string groupId) => $"{HoldingPrefix}{groupId}:";

    public static string CodedValue(Guid id) => $"{CodedValuePrefix}{id:D}";

    public static string Setting(string name) => $"{SettingPrefix}{name}";
}
