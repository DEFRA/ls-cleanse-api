using Cleanse.Api.Contracts;
using Cleanse.Auth;
using Cleanse.Core;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Commands.Holdings;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;
using Cleanse.Core.Queries.Holdings;

namespace Cleanse.Api.V1;

/// <summary>
/// The holding page: one CPH within a rule group, worked as a unit. The CPH travels in the query
/// string or the body, never the path, because it contains slashes.
/// </summary>
public static class HoldingEndpoints
{
    private const string ActingUserNote =
        " Send x-cleanse-user-id with a known user's id to record the change against that person; only ls-cleanse-ui may.";

    public static RouteGroupBuilder MapHoldingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/holdings").WithTags("Holdings");

        group.MapGet(string.Empty, Get)
            .WithName("GetHolding")
            .WithSummary("Everything recorded about one holding.")
            .WithDescription("Every issue at the CPH, open ones first, with the holding's status, assignee, notes and history, newest first. The status is worked out from the active issues: in-progress if any is In progress, else todo if any is To do, else suppressed if any is suppressed, else closed. 404 when no issue in the group has the CPH.")
            .Produces<HoldingDetailDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/assignee", Assign)
            .WithName("AssignHolding")
            .WithSummary("Assign a holding to a known user, or unassign it.")
            .WithDescription("A null userId unassigns. From then on the assignment belongs to Cleanse, and SPOL imports leave it alone. expectedVersion is the holding's version, 0 for one with nothing recorded yet." + ActingUserNote)
            .RequireAuthorization(CleansePolicies.UserDirectory)
            .Produces<HoldingDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/notes", AddNote)
            .WithName("AddHoldingNote")
            .WithSummary("Add a note to a holding.")
            .WithDescription("Notes are only ever added, never edited or removed." + ActingUserNote)
            .Produces<HoldingNoteDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/actions", RecordActions)
            .WithName("RecordHoldingActions")
            .WithSummary("Record what was done about a holding.")
            .WithDescription("One action per action type, all recorded together, with an optional note. Types must be active entries in the action-types list. Recording actions never changes an issue." + ActingUserNote)
            .Produces<RecordedHoldingActionsDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/workflow-status", UpdateIssues)
            .WithName("UpdateHoldingIssues")
            .WithSummary("Give several of a holding's issues one new status.")
            .WithDescription("All the issues change or none do. suppressed needs suppressUntilUtc; resolved may, and nonactionable (Can't resolve) must, name a reasonId from resolved-reasons or cant-resolve-reasons. 409 when an issue has changed since the page showed it; the problem's subject names the issue." + ActingUserNote)
            .Produces<HoldingIssuesUpdatedDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return group;
    }

    private static Result<HoldingKey> ParseKey(string? groupId, string? cph) =>
        string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(cph)
            ? DomainErrors.Validation("Supply both groupId and cph.")
            : new HoldingKey(groupId, cph);

    /// <summary>The holding a change is to, and who it is recorded against.</summary>
    private static async Task<Result<(HoldingKey Key, Actor Actor)>> TargetAsync(
        string? groupId, string? cph, HttpContext context, CancellationToken cancellationToken)
    {
        var key = ParseKey(groupId, cph);
        if (key.IsFailure)
        {
            return key.Error;
        }

        var actor = await ActingUser.ResolveAsync(context, cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error;
        }

        return (key.Value, actor.Value);
    }

    private static async Task<IResult> Get(
        string? groupId,
        string? cph,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var key = ParseKey(groupId, cph);
        if (key.IsFailure)
        {
            return key.Error.ToProblem();
        }

        var result = await dispatcher.QueryAsync(new GetHoldingQuery(key.Value), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> Assign(
        AssignHoldingRequest request,
        HttpContext context,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var target = await TargetAsync(request.GroupId, request.Cph, context, cancellationToken);
        if (target.IsFailure)
        {
            return target.Error.ToProblem();
        }

        var (key, actor) = target.Value;
        var result = await dispatcher.SendAsync(
            new AssignHoldingCommand(key, request.UserId, actor, request.ExpectedVersion),
            cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> AddNote(
        AddHoldingNoteRequest request,
        HttpContext context,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var target = await TargetAsync(request.GroupId, request.Cph, context, cancellationToken);
        if (target.IsFailure)
        {
            return target.Error.ToProblem();
        }

        var (key, actor) = target.Value;
        var result = await dispatcher.SendAsync(new AddHoldingNoteCommand(key, request.Text, actor), cancellationToken);
        return result.ToHttpResult(note => TypedResults.Created(HoldingUrl(key), note));
    }

    private static async Task<IResult> RecordActions(
        RecordHoldingActionsRequest request,
        HttpContext context,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var target = await TargetAsync(request.GroupId, request.Cph, context, cancellationToken);
        if (target.IsFailure)
        {
            return target.Error.ToProblem();
        }

        var (key, actor) = target.Value;
        var result = await dispatcher.SendAsync(
            new RecordHoldingActionsCommand(key, request.ActionTypeIds, request.Note, actor), cancellationToken);
        return result.ToHttpResult(recorded => TypedResults.Created(HoldingUrl(key), recorded));
    }

    private static async Task<IResult> UpdateIssues(
        UpdateHoldingIssuesRequest request,
        HttpContext context,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var target = await TargetAsync(request.GroupId, request.Cph, context, cancellationToken);
        if (target.IsFailure)
        {
            return target.Error.ToProblem();
        }

        if (!StatusNames.TryParseWorkflowStatus(request.WorkflowStatus, out var status))
        {
            return DomainErrors.Validation($"'{request.WorkflowStatus}' is not a known workflow status.").ToProblem();
        }

        if (request.Issues.Any(issue => string.IsNullOrWhiteSpace(issue.Id) || issue.ExpectedVersion is null))
        {
            return DomainErrors.Validation("Each issue needs an id and an expectedVersion.").ToProblem();
        }

        var issues = request.Issues
            .Select(issue => new IssueVersion(IssueId.From(issue.Id), issue.ExpectedVersion!.Value))
            .ToList();

        var (key, actor) = target.Value;
        var result = await dispatcher.SendAsync(
            new UpdateHoldingIssuesCommand(key, issues, status, actor, request.ReasonId, request.SuppressUntilUtc, request.Note),
            cancellationToken);
        return result.ToHttpResult();
    }

    private static string HoldingUrl(HoldingKey key) =>
        $"/v1/holdings?groupId={Uri.EscapeDataString(key.GroupId)}&cph={Uri.EscapeDataString(key.Cph)}";
}
