using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Queries.Imports;
using Microsoft.AspNetCore.Mvc;

namespace Cleanse.Api.V1;

/// <summary>
/// Import jobs: each processing run of a SPOL dataset, and the file uploads that came before them.
/// A job is started from its dataset, at POST /v1/datasets/{id}/process.
/// </summary>
public static class ImportEndpoints
{
    public static RouteGroupBuilder MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/imports").WithTags("Imports");

        group.MapGet(string.Empty, Search)
            .WithName("SearchImports")
            .WithSummary("List imports, newest first.")
            .WithDescription("groupId and datasetId narrow the list to one group's or one dataset's imports.")
            .Produces<PagedResult<ImportJobDto>>();
        group.MapGet("/{id:guid}", Get)
            .WithName("GetImport")
            .WithSummary("Import status and progress.")
            .Produces<ImportJobDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapGet("/{id:guid}/rejections", GetRejections)
            .WithName("GetImportRejections")
            .WithSummary("Rejected items and non-fatal warnings.")
            .WithDescription("rowNumber is the SharePoint item ID for a dataset's import, and the row in the file for an earlier upload.")
            .Produces<PagedResult<ImportRejectionDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> Get(
        Guid id,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(new GetImportJobQuery(id), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> Search(
        [FromQuery(Name = "groupId")] string? groupId,
        [FromQuery(Name = "datasetId")] Guid? datasetId,
        [AsParameters] PageParameters page,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(
            new SearchImportJobsQuery(groupId, page.ToPageRequest(), datasetId), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> GetRejections(
        Guid id,
        [AsParameters] PageParameters page,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(
            new GetImportRejectionsQuery(id, page.ToPageRequest()), cancellationToken);
        return result.ToHttpResult();
    }
}
