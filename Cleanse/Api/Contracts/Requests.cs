using System.ComponentModel.DataAnnotations;
using Cleanse.Core.Domain;

namespace Cleanse.Api.Contracts;

/// <summary>
/// The actor is taken from the authenticated caller, never from the request body, so a caller
/// cannot attribute their change to somebody else. A trusted front end names the person it acts
/// for in the x-cleanse-user-id header instead; see <see cref="ActingUser"/>.
/// </summary>
public sealed record SetNotesRequest(string? Notes, int? ExpectedVersion);

/// <summary>ReasonId names an entry in the outcome's reason list; Can't resolve (nonactionable) needs one.</summary>
public sealed record SetWorkflowStatusRequest(
    [property: Required] string WorkflowStatus,
    int? ExpectedVersion,
    Guid? ReasonId = null);

public sealed record SuppressIssueRequest(
    [property: Required] DateTimeOffset ExpiresAtUtc,
    int? ExpectedVersion);

public sealed record AddHistoryRequest(
    [property: Required] string ActionType,
    [property: Required, MaxLength(4000)] string Comments);

public sealed record StartRunRequest(
    [property: Required] string GroupId,
    string? RunId);

public sealed record AbandonRunRequest([property: Required, MaxLength(500)] string Reason);

public sealed record DetectionRequest(
    [property: Required] string Id,
    [property: Required] string RuleId,
    IReadOnlyDictionary<string, string>? Context)
{
    public IssueDetection ToDetection() => new(IssueId.From(Id), RuleId, Context);
}

public sealed record UpsertDetectionsRequest(
    [property: Required] IReadOnlyList<DetectionRequest> Detections);

/// <summary>Omitting GroupId clears every group. There is no undo.</summary>
public sealed record ResetRequest(string? GroupId);

/// <summary>
/// A holding is named in the body rather than the path, because a CPH contains slashes. A null
/// UserId unassigns it.
/// </summary>
public sealed record AssignHoldingRequest(
    [property: Required] string GroupId,
    [property: Required] string Cph,
    Guid? UserId,
    int? ExpectedVersion);

public sealed record AddHoldingNoteRequest(
    [property: Required] string GroupId,
    [property: Required] string Cph,
    [property: Required, MaxLength(HoldingNote.MaxLength)] string Text);

public sealed record RecordHoldingActionsRequest(
    [property: Required] string GroupId,
    [property: Required] string Cph,
    [property: Required] IReadOnlyList<Guid> ActionTypeIds,
    [property: MaxLength(HoldingNote.MaxLength)] string? Note);

/// <summary>An issue the holding page showed, at the version it showed.</summary>
public sealed record HoldingIssueRequest(
    [property: Required] string Id,
    [property: Required] int? ExpectedVersion);

/// <summary>
/// Gives the listed issues one new status, all or none. Suppressed needs SuppressUntilUtc;
/// resolved may and nonactionable (Can't resolve) must name a reason from its list.
/// </summary>
public sealed record UpdateHoldingIssuesRequest(
    [property: Required] string GroupId,
    [property: Required] string Cph,
    [property: Required] string WorkflowStatus,
    [property: Required] IReadOnlyList<HoldingIssueRequest> Issues,
    Guid? ReasonId,
    DateTimeOffset? SuppressUntilUtc,
    [property: MaxLength(HoldingNote.MaxLength)] string? Note);

public sealed record AddCodedValueRequest(
    [property: Required, MaxLength(CodedValue.MaxNameLength)] string Name);

public sealed record SetCodedValueActiveRequest([property: Required] bool? IsActive);

public sealed record UpdateSettingsRequest(
    [property: Required, Range(0, ServiceSettings.MaxMinimumIssueAgeDays)] int? MinimumIssueAgeDays);
