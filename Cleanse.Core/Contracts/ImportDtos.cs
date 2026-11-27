using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Imports;

namespace Cleanse.Core.Contracts;

/// <summary>
/// One row of an external extract, already parsed and normalised. Carries only human-axis state
/// plus the contact dates, so an import can never disturb what the rules engine owns.
/// </summary>
public sealed record ExternalIssueRow
{
    /// <summary>The SharePoint item ID, or an earlier upload's row in its file.</summary>
    public required long RowNumber { get; init; }

    public required IssueId IssueId { get; init; }

    public WorkflowStatus? WorkflowStatus { get; init; }

    public string? ExternalNotes { get; init; }

    public bool HasAssignee { get; init; }

    /// <summary>The assignee as SPOL names them, or their email address when that is all it gives.</summary>
    public string? AssignedTo { get; init; }

    /// <summary>Matched before <see cref="AssignedTo"/>, which is then matched by name only.</summary>
    public string? AssignedEmail { get; init; }

    public IssuePriority? Priority { get; init; }

    public DateTimeOffset? ReceivedAtUtc { get; init; }

    /// <summary>Dated contact events, mapped straight from the source columns with no inference
    /// about what the contact actually was.</summary>
    public IReadOnlyList<ExternalContactEvent> Contacts { get; init; } = [];

    internal ExternalIssueState ToState() => new()
    {
        WorkflowStatus = WorkflowStatus,
        ExternalNotes = ExternalNotes,
        Priority = Priority,
        ReceivedAtUtc = ReceivedAtUtc
    };
}

public sealed record ExternalContactEvent(string Column, HistoryActionType ActionType, DateTimeOffset AtUtc);

/// <summary>Someone an extract assigns work to. One with no email address is matched by name only.</summary>
public sealed record ExternalAssignee(string? Email, string? Name);

public sealed record ImportBatchResult(
    int RowsProcessed,
    int Matched,
    int Unmatched,
    int HistoryEntriesWritten)
{
    public static readonly ImportBatchResult Empty = new(0, 0, 0, 0);
}

public sealed record ImportJobDto(
    Guid Id,
    string GroupId,
    string FileName,
    string Status,
    int TotalRows,
    int ProcessedRows,
    int PercentComplete,
    int Matched,
    int Unmatched,
    int DuplicatesCollapsed,
    int Invalid,
    string RequestedBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    string? FailureReason,
    Guid? DatasetId,
    int UsersCreated);

public sealed record ImportRejectionDto(long RowNumber, string? IssueId, string Reason, string Detail);
