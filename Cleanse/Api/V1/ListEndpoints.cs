using Cleanse.Api.Contracts;
using Cleanse.Auth;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Commands.CodedValues;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;
using Cleanse.Core.Queries.CodedValues;

namespace Cleanse.Api.V1;

/// <summary>
/// The admin-maintained lists: action-types, resolved-reasons and cant-resolve-reasons. Entries
/// are added and switched on or off, never renamed or deleted, so what was recorded against one
/// keeps its meaning. Only ls-cleanse-ui may change them; it decides who counts as an admin.
/// </summary>
public static class ListEndpoints
{
    public static RouteGroupBuilder MapListEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/lists").WithTags("Lists");

        group.MapGet("/{list}", List)
            .WithName("ListCodedValues")
            .WithSummary("Every entry in a list, ordered by name.")
            .WithDescription("activeOnly=true leaves out entries that are switched off, which is what a form offers.")
            .Produces<IReadOnlyList<CodedValueDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{list}", Add)
            .WithName("AddCodedValue")
            .WithSummary("Add an entry to a list.")
            .WithDescription("New entries are active. 409 when the list already has an entry of that name, ignoring case and surrounding spaces. Send x-cleanse-user-id to record the admin who added it.")
            .RequireAuthorization(CleansePolicies.UserDirectory)
            .Produces<CodedValueDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/{list}/{id:guid}/active", SetActive)
            .WithName("SetCodedValueActive")
            .WithSummary("Switch a list entry on or off.")
            .WithDescription("An entry switched off is no longer offered, but stays on everything it was recorded against. Send x-cleanse-user-id to record the admin who changed it.")
            .RequireAuthorization(CleansePolicies.UserDirectory)
            .Produces<CodedValueDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static IResult UnknownList(string list) =>
        TypedResults.Problem(
            title: "Not found",
            detail: $"'{list}' is not a list. Use {CodedLists.ActionTypes}, {CodedLists.ResolvedReasons} or {CodedLists.CantResolveReasons}.",
            statusCode: StatusCodes.Status404NotFound);

    private static async Task<IResult> List(
        string list,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken,
        bool activeOnly = false)
    {
        if (!CodedLists.TryParse(list, out var parsed))
        {
            return UnknownList(list);
        }

        var result = await dispatcher.QueryAsync(new ListCodedValuesQuery(parsed, activeOnly), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> Add(
        string list,
        AddCodedValueRequest request,
        HttpContext context,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        if (!CodedLists.TryParse(list, out var parsed))
        {
            return UnknownList(list);
        }

        var actor = await ActingUser.ResolveAsync(context, cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error.ToProblem();
        }

        var result = await dispatcher.SendAsync(new AddCodedValueCommand(parsed, request.Name, actor.Value), cancellationToken);
        return result.ToHttpResult(value => TypedResults.Created($"/v1/lists/{value.List}", value));
    }

    private static async Task<IResult> SetActive(
        string list,
        Guid id,
        SetCodedValueActiveRequest request,
        HttpContext context,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        if (!CodedLists.TryParse(list, out var parsed))
        {
            return UnknownList(list);
        }

        var actor = await ActingUser.ResolveAsync(context, cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error.ToProblem();
        }

        var result = await dispatcher.SendAsync(
            new SetCodedValueActiveCommand(parsed, id, request.IsActive!.Value, actor.Value), cancellationToken);
        return result.ToHttpResult();
    }
}
