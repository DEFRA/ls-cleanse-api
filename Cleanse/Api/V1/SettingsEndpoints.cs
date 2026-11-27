using Cleanse.Api.Contracts;
using Cleanse.Auth;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Commands.Settings;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;
using Cleanse.Core.Queries.Settings;

namespace Cleanse.Api.V1;

/// <summary>
/// The service-wide settings. Any caller may read them; only ls-cleanse-ui may change them, and it
/// decides who counts as an admin.
/// </summary>
public static class SettingsEndpoints
{
    public static RouteGroupBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/settings").WithTags("Settings");

        group.MapGet(string.Empty, Get)
            .WithName("GetSettings")
            .WithSummary("The service-wide settings.")
            .WithDescription($"minimumIssueAgeDays is how many days old an issue must be before a search with applyMinimumAge=true lists it. It is {ServiceSettings.DefaultMinimumIssueAgeDays} until an admin changes it, and 0 lists every issue.")
            .Produces<SettingsDto>();

        group.MapPut(string.Empty, Update)
            .WithName("UpdateSettings")
            .WithSummary("Change the service-wide settings.")
            .WithDescription($"minimumIssueAgeDays must be a whole number from 0 to {ServiceSettings.MaxMinimumIssueAgeDays}. Send x-cleanse-user-id to record the admin who changed it.")
            .RequireAuthorization(CleansePolicies.UserDirectory)
            .Produces<SettingsDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return group;
    }

    private static async Task<IResult> Get(ICleanseDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(new GetSettingsQuery(), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> Update(
        UpdateSettingsRequest request,
        HttpContext context,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var actor = await ActingUser.ResolveAsync(context, cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error.ToProblem();
        }

        var result = await dispatcher.SendAsync(
            new UpdateSettingsCommand(request.MinimumIssueAgeDays!.Value, actor.Value), cancellationToken);
        return result.ToHttpResult();
    }
}
