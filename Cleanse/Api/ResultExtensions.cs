using Cleanse.Core;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Cleanse.Api;

/// <summary>
/// Maps Core's stable error codes onto HTTP. This is the only place the API tier decides what a
/// domain failure means over the wire, which is why the codes were made stable in the first place.
/// </summary>
public static class ResultExtensions
{
    public static IResult ToHttpResult<T>(this Result<T> result, Func<T, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onSuccess);

        return result.IsSuccess ? onSuccess(result.Value) : result.Error.ToProblem();
    }

    public static IResult ToHttpResult<T>(this Result<T> result) =>
        result.ToHttpResult(TypedResults.Ok);

    public static ProblemHttpResult ToProblem(this DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var status = error.Code switch
        {
            ErrorCodes.IssueNotFound
                or ErrorCodes.RunNotFound
                or ErrorCodes.HoldingNotFound
                or ErrorCodes.CodedValueNotFound
                or ErrorCodes.DatasetNotFound => StatusCodes.Status404NotFound,

            ErrorCodes.IssueConcurrencyConflict
                or ErrorCodes.HoldingConcurrencyConflict
                or ErrorCodes.CodedValueDuplicateName
                or ErrorCodes.RunAlreadyInProgress
                or ErrorCodes.RunNotRunning
                or ErrorCodes.IssueGroupMismatch
                or ErrorCodes.ImportAlreadyInProgress
                or ErrorCodes.DatasetConcurrencyConflict
                or ErrorCodes.DatasetLoadSuperseded
                or ErrorCodes.DatasetProcessing
                or ErrorCodes.DatasetNotReady => StatusCodes.Status409Conflict,

            // The same for an unknown dataset as for a wrong key.
            ErrorCodes.DatasetUnauthorised => StatusCodes.Status401Unauthorized,

            ErrorCodes.ActingUserNotAllowed => StatusCodes.Status403Forbidden,

            ErrorCodes.DatasetTooLarge => StatusCodes.Status413PayloadTooLarge,

            // Not the caller's fault and not permanent: the engine is switched off here.
            ErrorCodes.RulesEngineDisabled => StatusCodes.Status503ServiceUnavailable,

            _ => StatusCodes.Status400BadRequest
        };

        var problem = new ProblemDetails
        {
            Title = TitleFor(status),
            Detail = error.Message,
            Status = status,
            Extensions = { ["code"] = error.Code }
        };

        // Which item failed, where a request names several: the issue that could not move, say.
        if (error.Subject is not null)
        {
            problem.Extensions["subject"] = error.Subject;
        }

        return TypedResults.Problem(problem);
    }

    private static string TitleFor(int status) => status switch
    {
        StatusCodes.Status401Unauthorized => "Unauthorized",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "Not found",
        StatusCodes.Status409Conflict => "Conflict",
        StatusCodes.Status413PayloadTooLarge => "Too large",
        StatusCodes.Status503ServiceUnavailable => "Unavailable",
        _ => "Invalid request"
    };
}
