using System.Globalization;
using Cleanse.Api.Mi;
using Cleanse.Auth;
using Cleanse.Core;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Queries.Mi;
using Cleanse.Rules;
using Microsoft.AspNetCore.Mvc;

namespace Cleanse.Api.V1;

/// <summary>
/// The MI dashboard's figures, read from a summary that a background refresh keeps up to date.
/// The work figures name people, so only a caller trusted with the user directory may read them.
/// </summary>
public static class MiEndpoints
{
    private const string PeriodDescription =
        "from and to are UK dates (yyyy-MM-dd), both included, and default to the last 7 days. interval is " +
        "day, week (from Monday) or month, chosen from the period's length when not given. A period after " +
        "today, more than 400 points, or a malformed value is a 400 saying why. The period before, of the " +
        "same length, is given for comparison.";

    public static RouteGroupBuilder MapMiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/groups/{groupId}/mi").WithTags("MI dashboard");

        group.MapGet("/work", GetWork)
            .RequireAuthorization(CleansePolicies.UserDirectory)
            .WithName("GetMiWork")
            .WithSummary("The work done in Cleanse over a period, by person, action type, reason and rule.")
            .WithDescription(
                PeriodDescription + " ruleId narrows the outcomes to one rule; userId to one person. Only " +
                "changes people made in Cleanse count: those from the rules, SPOL and suppressions ending do not. " +
                "It names people, so it needs the user directory policy.")
            .Produces<MiWorkDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/backlog", GetBacklog)
            .WithName("GetMiBacklog")
            .WithSummary("How the group's issues, or holdings, stood over a period, whoever changed them.")
            .WithDescription(
                PeriodDescription + " unit is issues (the default) or holdings. ruleId narrows issues to one " +
                "rule, and cannot be used with holdings.")
            .Produces<MiBacklogDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/status", GetStatus)
            .WithName("GetMiStatus")
            .WithSummary("When the figures were last brought up to date, and whether they are being rebuilt.")
            .WithDescription("Cheap enough to ask every minute.")
            .Produces<MiStatusDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/rebuild", Rebuild)
            .RequireAuthorization(CleansePolicies.UserDirectory)
            .WithName("RebuildMi")
            .WithSummary("Rebuild the figures from the history, in the background.")
            .WithDescription(
                "Returns at once; the status says when the rebuild has finished, and the old figures are shown " +
                "until then. Send x-cleanse-user-id to record the admin who asked. 503 where the refresh is " +
                "turned off.")
            .Produces<MiStatusDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return group;
    }

    private static async Task<IResult> GetWork(
        string groupId,
        [AsParameters] MiWorkParameters parameters,
        HttpContext context,
        CleanseRulesOptions rules,
        MiRefreshSignal signal,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        if (Known(groupId, rules) is { } unknown)
        {
            return unknown;
        }

        if (!TryDay(parameters.From, "from", out var from, out var error) ||
            !TryDay(parameters.To, "to", out var to, out error) ||
            !TryUser(parameters.UserId, out var userId, out error))
        {
            return DomainErrors.Validation(error!).ToProblem();
        }

        var result = await dispatcher.QueryAsync(
            new GetMiWorkQuery(groupId, from, to, parameters.Interval, parameters.RuleId, userId), cancellationToken);
        context.Response.Headers.CacheControl = "no-store";
        return result.ToHttpResult(work => TypedResults.Ok(work with { Status = work.Status with { Building = signal.Building } }));
    }

    private static async Task<IResult> GetBacklog(
        string groupId,
        [AsParameters] MiBacklogParameters parameters,
        HttpContext context,
        CleanseRulesOptions rules,
        MiRefreshSignal signal,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        if (Known(groupId, rules) is { } unknown)
        {
            return unknown;
        }

        if (!TryDay(parameters.From, "from", out var from, out var error) ||
            !TryDay(parameters.To, "to", out var to, out error))
        {
            return DomainErrors.Validation(error!).ToProblem();
        }

        var result = await dispatcher.QueryAsync(
            new GetMiBacklogQuery(groupId, from, to, parameters.Interval, parameters.RuleId, parameters.Unit),
            cancellationToken);
        context.Response.Headers.CacheControl = "no-store";
        return result.ToHttpResult(backlog => TypedResults.Ok(backlog with { Status = backlog.Status with { Building = signal.Building } }));
    }

    private static async Task<IResult> GetStatus(
        string groupId,
        HttpContext context,
        CleanseRulesOptions rules,
        MiRefreshSignal signal,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        if (Known(groupId, rules) is { } unknown)
        {
            return unknown;
        }

        var result = await dispatcher.QueryAsync(new GetMiStatusQuery(groupId), cancellationToken);
        context.Response.Headers.CacheControl = "no-store";
        return result.ToHttpResult(status => TypedResults.Ok(status with { Building = signal.Building }));
    }

    private static async Task<IResult> Rebuild(
        string groupId,
        HttpContext context,
        CleanseRulesOptions rules,
        MiRefreshOptions options,
        MiRefreshSignal signal,
        ICleanseDispatcher dispatcher,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (Known(groupId, rules) is { } unknown)
        {
            return unknown;
        }

        if (!options.Enabled)
        {
            return DomainErrors.MiRefreshDisabled().ToProblem();
        }

        var actor = await ActingUser.ResolveAsync(context, cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error.ToProblem();
        }

        signal.RequestRebuild();
        loggerFactory.CreateLogger("Cleanse.Api.Mi").LogInformation(
            "{ActorId} asked for the dashboard figures for {GroupId} to be rebuilt.", actor.Value.Id, groupId);

        var result = await dispatcher.QueryAsync(new GetMiStatusQuery(groupId), cancellationToken);
        return result.ToHttpResult(status => TypedResults.Accepted(
            $"/v1/groups/{Uri.EscapeDataString(groupId)}/mi/status", status with { Building = true }));
    }

    /// <summary>The deployment's own group is the only one there is figures for.</summary>
    private static IResult? Known(string groupId, CleanseRulesOptions rules) =>
        string.Equals(groupId, rules.GroupId, StringComparison.Ordinal)
            ? null
            : DomainErrors.GroupNotFound(groupId).ToProblem();

    private static bool TryDay(string? value, string name, out DateOnly? day, out string? error)
    {
        day = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            day = parsed;
            return true;
        }

        error = $"{name} must be a date as yyyy-MM-dd, not '{value}'.";
        return false;
    }

    private static bool TryUser(string? value, out Guid? userId, out string? error)
    {
        userId = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (Guid.TryParse(value, out var parsed) && parsed != Guid.Empty)
        {
            userId = parsed;
            return true;
        }

        error = "userId must be a nonempty GUID.";
        return false;
    }
}

public sealed record MiWorkParameters(
    [FromQuery(Name = "from")] string? From,
    [FromQuery(Name = "to")] string? To,
    [FromQuery(Name = "interval")] string? Interval,
    [FromQuery(Name = "ruleId")] string? RuleId,
    [FromQuery(Name = "userId")] string? UserId);

public sealed record MiBacklogParameters(
    [FromQuery(Name = "from")] string? From,
    [FromQuery(Name = "to")] string? To,
    [FromQuery(Name = "interval")] string? Interval,
    [FromQuery(Name = "ruleId")] string? RuleId,
    [FromQuery(Name = "unit")] string? Unit);
