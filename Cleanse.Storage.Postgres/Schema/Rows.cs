namespace Cleanse.Storage.Postgres.Schema;

/// <summary>
/// The physical row shapes. Deliberately separate from the domain snapshots: these carry
/// storage-only concerns such as the identity <c>Sequence</c> column, and keep jsonb payloads as
/// raw text so Npgsql maps them directly without a value converter.
/// </summary>
internal sealed class IssueRow
{
    public required string Id { get; set; }

    public required string GroupId { get; set; }

    public required string RuleId { get; set; }

    public required string RunId { get; set; }

    public required string Status { get; set; }

    public required string WorkflowStatus { get; set; }

    public string? AssignedTo { get; set; }

    public Guid? AssignedUserId { get; set; }

    public string? AssignedUserFullName { get; set; }

    public DateTimeOffset? AssignmentDetachedAtUtc { get; set; }

    public string? Notes { get; set; }

    public string? ExternalNotes { get; set; }

    public string? Priority { get; set; }

    public DateTimeOffset? ExternalReceivedAtUtc { get; set; }

    public DateTimeOffset? SuppressionExpiresAtUtc { get; set; }

    public required string Context { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public DateTimeOffset ActivatedAtUtc { get; set; }

    public DateTimeOffset? DeactivatedAtUtc { get; set; }

    public int Version { get; set; }

    public Guid? OutcomeReasonId { get; set; }

    public string? OutcomeReason { get; set; }
}

/// <summary>A holding with something recorded against it. Keyed by group and CPH.</summary>
internal sealed class HoldingRow
{
    public required string GroupId { get; set; }

    public required string Cph { get; set; }

    public string? AssignedTo { get; set; }

    public Guid? AssignedUserId { get; set; }

    public string? AssignedUserFullName { get; set; }

    public DateTimeOffset? AssignmentDetachedAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public int Version { get; set; }
}

internal sealed class HoldingNoteRow
{
    public Guid Id { get; set; }

    /// <summary>Database-generated. The stable tie-break for notes sharing a timestamp.</summary>
    public long Sequence { get; set; }

    public required string GroupId { get; set; }

    public required string Cph { get; set; }

    public required string Text { get; set; }

    public string? Context { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public required string CreatedById { get; set; }

    public required string CreatedByDisplayName { get; set; }

    public required string CreatedByKind { get; set; }
}

internal sealed class HoldingActionRow
{
    public Guid Id { get; set; }

    public long Sequence { get; set; }

    public Guid BatchId { get; set; }

    public required string GroupId { get; set; }

    public required string Cph { get; set; }

    public Guid ActionTypeId { get; set; }

    public required string ActionTypeName { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public required string CreatedById { get; set; }

    public required string CreatedByDisplayName { get; set; }

    public required string CreatedByKind { get; set; }
}

internal sealed class CodedValueRow
{
    public Guid Id { get; set; }

    public required string List { get; set; }

    public required string Name { get; set; }

    /// <summary>The name lower-cased and trimmed, so a plain unique index ignores case and spaces.</summary>
    public required string NameKey { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>The one row of service-wide settings. Its id is always 1.</summary>
internal sealed class ServiceSettingsRow
{
    public int Id { get; set; }

    public int MinimumIssueAgeDays { get; set; }
}

/// <summary>One recorded reset. Survives the reset it records.</summary>
internal sealed class DataResetRow
{
    public long Sequence { get; set; }

    public DateTimeOffset AtUtc { get; set; }

    public required string Scope { get; set; }

    public required string ActorId { get; set; }

    public int Issues { get; set; }
}

internal sealed class RuleRunRow
{
    public required string RunId { get; set; }

    public required string GroupId { get; set; }

    public required string State { get; set; }

    public DateTimeOffset StartedAtUtc { get; set; }

    public DateTimeOffset? FinishedAtUtc { get; set; }

    public string? AbandonReason { get; set; }

    public int DetectionsProcessed { get; set; }

    public int IssuesCreated { get; set; }

    public int IssuesReactivated { get; set; }

    public int IssuesUpdated { get; set; }

    public int IssuesUnchanged { get; set; }

    public int IssuesDeactivated { get; set; }

    public string? CurrentStage { get; set; }

    public int RecordsProcessed { get; set; }

    public int RecordsTotal { get; set; }

    public DateTimeOffset? HeartbeatAtUtc { get; set; }

    public bool CancellationRequested { get; set; }

    public string? FailureMessage { get; set; }

    public string? FailureDetail { get; set; }

    public int Version { get; set; }
}

internal sealed class AuditEntryRow
{
    public Guid Id { get; set; }

    /// <summary>Database-generated. The stable tie-break for entries sharing a timestamp.</summary>
    public long Sequence { get; set; }

    public string? IssueId { get; set; }

    public string? RunId { get; set; }

    public required string Kind { get; set; }

    public required string ActorId { get; set; }

    public required string ActorDisplayName { get; set; }

    public required string ActorKind { get; set; }

    public DateTimeOffset AtUtc { get; set; }

    public required string Changes { get; set; }

    public string? Subject { get; set; }
}

internal sealed class IssueHistoryRow
{
    public Guid Id { get; set; }

    public long Sequence { get; set; }

    public required string IssueId { get; set; }

    public required string ActionType { get; set; }

    public required string Comments { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public required string CreatedById { get; set; }

    public required string CreatedByDisplayName { get; set; }

    public required string CreatedByKind { get; set; }
}

internal sealed class ImportJobRow
{
    public string? TenantId { get; set; }

    public string? ClientId { get; set; }

    public Guid Id { get; set; }

    public required string GroupId { get; set; }

    public required string FileName { get; set; }

    public string? BlobKey { get; set; }

    public Guid? DatasetId { get; set; }

    public int UsersCreated { get; set; }

    public required string Status { get; set; }

    public int TotalRows { get; set; }

    public int ProcessedRows { get; set; }

    public int Matched { get; set; }

    public int Unmatched { get; set; }

    public int DuplicatesCollapsed { get; set; }

    public int Invalid { get; set; }

    public required string RequestedBy { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? StartedAtUtc { get; set; }

    public DateTimeOffset? FinishedAtUtc { get; set; }

    public DateTimeOffset? HeartbeatAtUtc { get; set; }

    public string? FailureReason { get; set; }

    public int Version { get; set; }
}

internal sealed class ImportJobRejectionRow
{
    public long Sequence { get; set; }

    public Guid ImportJobId { get; set; }

    public long RowNumber { get; set; }

    public string? IssueId { get; set; }

    public required string Reason { get; set; }

    public required string Detail { get; set; }
}

internal sealed class SpolDatasetRow
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public required string KeyHash { get; set; }

    public required string Status { get; set; }

    public int RowCount { get; set; }

    public int? ExpectedRows { get; set; }

    public int? PageCount { get; set; }

    public Guid? CurrentLoadId { get; set; }

    public DateTimeOffset? LoadStartedAtUtc { get; set; }

    public DateTimeOffset? LastLoadedAtUtc { get; set; }

    public required string CreatedById { get; set; }

    public required string CreatedByName { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public int Version { get; set; }
}

internal sealed class SpolDatasetItemRow
{
    public Guid DatasetId { get; set; }

    public int ItemId { get; set; }

    public required string Data { get; set; }
}

internal sealed class MiRefreshRow
{
    public required string GroupId { get; set; }

    public DateTimeOffset BuiltAtUtc { get; set; }

    public DateTimeOffset CheckedAtUtc { get; set; }

    public required string Fingerprint { get; set; }

    public int Version { get; set; }

    public int DurationMilliseconds { get; set; }

    public DateTimeOffset? HistoryFromUtc { get; set; }

    public int Issues { get; set; }

    public int Events { get; set; }

    public int Mismatches { get; set; }
}

internal sealed class MiIssueStateRow
{
    public required string GroupId { get; set; }

    public required string IssueId { get; set; }

    public required string RuleId { get; set; }

    public string? Cph { get; set; }

    public required string Status { get; set; }

    public required string WorkflowStatus { get; set; }

    public Guid? ReasonId { get; set; }

    public string? ReasonName { get; set; }

    public DateTimeOffset FirstRaisedAtUtc { get; set; }

    public DateOnly FirstRaisedDay { get; set; }

    public DateTimeOffset LastChangedAtUtc { get; set; }
}

internal sealed class MiHoldingStateRow
{
    public required string GroupId { get; set; }

    public required string Cph { get; set; }

    public required string Status { get; set; }

    public bool HasActiveIssue { get; set; }

    public int OpenIssues { get; set; }

    public DateTimeOffset? OldestOpenRaisedAtUtc { get; set; }

    public DateOnly? OldestOpenRaisedDay { get; set; }

    public DateTimeOffset? InProgressSinceUtc { get; set; }

    public DateTimeOffset? LastWorkedAtUtc { get; set; }

    public Guid? AssignedUserId { get; set; }

    public string? AssigneeName { get; set; }
}

internal sealed class MiEventRow
{
    public required string GroupId { get; set; }

    public long Sequence { get; set; }

    public DateTimeOffset AtUtc { get; set; }

    public DateOnly Day { get; set; }

    public required string Unit { get; set; }

    public required string Kind { get; set; }

    public required string Source { get; set; }

    public string? IssueId { get; set; }

    public string? RuleId { get; set; }

    public string? Cph { get; set; }

    public Guid? UserId { get; set; }

    public string? UserName { get; set; }

    public string? FromStatus { get; set; }

    public string? ToStatus { get; set; }

    public string? FromWorkflow { get; set; }

    public string? ToWorkflow { get; set; }

    public Guid? ReasonId { get; set; }

    public string? ReasonName { get; set; }

    public string? OpenChange { get; set; }

    public int OpenDelta { get; set; }

    public int? ElapsedDays { get; set; }

    public bool? InactiveNow { get; set; }

    public int? ActionsSinceClosed { get; set; }
}

internal sealed class MiDailyChangeRow
{
    public required string GroupId { get; set; }

    public required string Unit { get; set; }

    public DateOnly Day { get; set; }

    public required string RuleId { get; set; }

    public required string Status { get; set; }

    public required string State { get; set; }

    public int Delta { get; set; }
}
