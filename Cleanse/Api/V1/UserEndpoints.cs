using Cleanse.Auth;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;

namespace Cleanse.Api.V1;

public static class UserEndpoints
{
    public static RouteGroupBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/users").WithTags("Users")
            .RequireAuthorization(CleansePolicies.UserDirectory);

        group.MapPost("/logins", RecordLogin)
            .WithName("RecordUserLogin")
            .WithSummary("Record a validated Entra login attested by ls-cleanse-ui.")
            .WithDescription("Resolves the sign-in to one user: the same issuer, clientId and subject; else the same tenantId and objectId; else a user who has not signed in yet whose email matches the email or preferredUsername claim; else a new user. Requires ls-cleanse-ui service identity; API keys require Development or explicit local Docker trust configuration.")
            .Produces<UserDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost(string.Empty, Register)
            .WithName("RegisterUsers")
            .WithSummary("Add people by email address before they first sign in.")
            .WithDescription($"Names are derived from the address. Addresses already known are returned as existing rather than duplicated. At most {RegisterUsersRequest.MaxEmails} per request; any invalid address rejects the whole request.")
            .Produces<RegisterUsersResultDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet(string.Empty, Search)
            .WithName("SearchUsers")
            .WithSummary("Page every known user, ordered by name, whether or not they have signed in.")
            .Produces<PagedResult<UserDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
        return group;
    }

    private static async Task<IResult> RecordLogin(
        RecordUserLoginRequest request, IUserDirectory directory, CancellationToken cancellationToken)
    {
        var result = await directory.RecordLoginAsync(request, cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> Register(
        RegisterUsersRequest request, IUserDirectory directory, CancellationToken cancellationToken)
    {
        var result = await directory.RegisterAsync(request, cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> Search(
        IUserDirectory directory, CancellationToken cancellationToken,
        int? pageNumber = null, int? page = null, int pageSize = 50)
    {
        var number = pageNumber ?? page ?? 1;
        if (number < 1 || pageSize < 1 || pageSize > PageRequest.MaxPageSize ||
            (long)(number - 1) * pageSize > int.MaxValue ||
            (pageNumber.HasValue && page.HasValue && pageNumber != page))
        {
            return TypedResults.Problem(title: "Invalid request", statusCode: 400,
                detail: "Use a positive pageNumber and pageSize from 1 to 500; page is an alias and must agree.");
        }

        var result = await directory.SearchAsync(new PageRequest(number, pageSize), cancellationToken);
        return result.ToHttpResult();
    }
}
