using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cleanse.Api.Contracts;
using Cleanse.Auth;
using Cleanse.Core;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Commands.Datasets;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain.Datasets;
using Cleanse.Core.Queries.Datasets;
using Microsoft.AspNetCore.Mvc;

namespace Cleanse.Api.V1;

/// <summary>
/// SPOL datasets: staging areas that ls-cleanse-spol-connector loads, through ls-cleanse-ui, for an
/// admin to process. Only ls-cleanse-ui may call these; it decides who counts as an admin, and
/// passes the connector's pages on with the dataset's key.
/// </summary>
public static class DatasetEndpoints
{
    /// <summary>The dataset's key, as the connector sends it.</summary>
    public const string KeyHeader = "x-dataset-key";

    private static readonly JsonSerializerOptions PageJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static RouteGroupBuilder MapDatasetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/datasets").WithTags("SPOL datasets")
            .RequireAuthorization(CleansePolicies.UserDirectory);

        group.MapPost(string.Empty, Create)
            .WithName("CreateSpolDataset")
            .WithSummary("Create an Empty dataset, and get its key.")
            .WithDescription("The key is in this response and nowhere else: Cleanse keeps only its hash. Without a name, the dataset is named after when it was created. Send x-cleanse-user-id to record the admin who created it.")
            .Produces<CreatedSpolDatasetDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet(string.Empty, Search)
            .WithName("SearchSpolDatasets")
            .WithSummary("List datasets, newest first.")
            .Produces<PagedResult<SpolDatasetDto>>();

        group.MapGet("/{id:guid}", Get)
            .WithName("GetSpolDataset")
            .WithSummary("A dataset's name, status and row count. Never its rows.")
            .WithDescription($"With {KeyHeader}, as the connector asks, an unknown dataset and a wrong key both get 401. Without it, an unknown dataset gets 404.")
            .Produces<SpolDatasetDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}", LoadPage)
            .WithName("LoadSpolDatasetPage")
            .WithSummary("Store one page of a load, with the dataset's key.")
            .WithDescription($"Needs {KeyHeader}. The first page of a new loadId replaces the dataset's rows, and a page from an older load is refused. Rows are stored by itemId, so a page sent twice changes nothing; the dataset is Ready when its rows reach totalRows. A page has 1 to {SpolDataset.MaxRowsPerPage} rows, and a dataset up to {SpolDataset.MaxRows}. Unknown properties, dates without an offset and values over {SpolDatasetRows.MaxValueLength} characters refuse the whole page.")
            .Accepts<SpolDatasetPageRequest>("application/json")
            .Produces<SpolDatasetPageResultDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        group.MapPut("/{id:guid}/name", Rename)
            .WithName("RenameSpolDataset")
            .WithSummary("Rename a dataset.")
            .WithDescription($"A name is required and can be up to {SpolDataset.MaxNameLength} characters. 409 when the dataset has changed since expectedVersion.")
            .Produces<SpolDatasetDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{id:guid}", Delete)
            .WithName("DeleteSpolDataset")
            .WithSummary("Delete a dataset and its rows.")
            .WithDescription("Its import jobs, and their reports, are kept. 409 while it is being processed.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/process", Process)
            .WithName("ProcessSpolDataset")
            .WithSummary("Process a Ready dataset into a group's issues and holdings.")
            .WithDescription("Starts an import job, which applies the rows in the background; follow it at /v1/imports/{id}. 409 unless the dataset is Ready, or while the group has an import in flight. Send x-cleanse-user-id to record the admin who asked.")
            .Produces<ImportJobDto>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return group;
    }

    private static async Task<IResult> Create(
        CreateSpolDatasetRequest request,
        HttpContext context,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var actor = await ActingUser.ResolveAsync(context, cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error.ToProblem();
        }

        var result = await dispatcher.SendAsync(new CreateSpolDatasetCommand(request.Name, actor.Value), cancellationToken);

        // The key is in the body, so nothing on the way may keep a copy.
        context.Response.Headers.CacheControl = "no-store";
        return result.ToHttpResult(created => TypedResults.Created($"/v1/datasets/{created.Dataset.Id}", created));
    }

    private static async Task<IResult> Search(
        [AsParameters] PageParameters page,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(new SearchSpolDatasetsQuery(page.ToPageRequest()), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> Get(
        Guid id,
        [FromHeader(Name = KeyHeader)] string? key,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(new GetSpolDatasetQuery(id, key), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> LoadPage(
        Guid id,
        HttpRequest request,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var key = request.Headers[KeyHeader].ToString();
        if (string.IsNullOrEmpty(key))
        {
            return DomainErrors.DatasetUnauthorised().ToProblem();
        }

        if (!request.HasJsonContentType())
        {
            return Problem("Send the page as application/json.", StatusCodes.Status415UnsupportedMediaType);
        }

        PageEnvelope? envelope;
        try
        {
            envelope = await JsonSerializer.DeserializeAsync<PageEnvelope>(request.Body, PageJson, cancellationToken);
        }
        catch (JsonException ex)
        {
            return Problem($"The page is not valid JSON of the expected shape, at {ex.Path ?? "$"}.", StatusCodes.Status400BadRequest);
        }

        if (envelope?.Rows is null)
        {
            return Problem("The page has no rows.", StatusCodes.Status400BadRequest);
        }

        var rows = new List<SpolDatasetRowDto?>(envelope.Rows.Count);
        for (var index = 0; index < envelope.Rows.Count; index++)
        {
            if (!TryReadRow(envelope.Rows[index], index, out var row, out var error))
            {
                return Problem(error, StatusCodes.Status400BadRequest);
            }

            rows.Add(row);
        }

        var result = await dispatcher.SendAsync(
            new LoadSpolDatasetPageCommand(
                id, key, envelope.LoadId, envelope.Page, envelope.PageCount, envelope.TotalRows, rows),
            cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> Rename(
        Guid id,
        RenameSpolDatasetRequest request,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            new RenameSpolDatasetCommand(id, request.Name, request.ExpectedVersion!.Value), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> Delete(
        Guid id,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(new DeleteSpolDatasetCommand(id), cancellationToken);
        return result.ToHttpResult(_ => TypedResults.NoContent());
    }

    private static async Task<IResult> Process(
        Guid id,
        ProcessSpolDatasetRequest request,
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
            new ProcessSpolDatasetCommand(id, request.GroupId, actor.Value), cancellationToken);
        return result.ToHttpResult(job => TypedResults.Accepted($"/v1/imports/{job.Id}", job));
    }

    /// <summary>
    /// Reads one row, naming it by its item ID where it has one, so that a bad row in a page of
    /// 200 can be found in SharePoint.
    /// </summary>
    private static bool TryReadRow(JsonElement element, int index, out SpolDatasetRowDto? row, out string error)
    {
        row = null;
        error = string.Empty;
        if (element.ValueKind != JsonValueKind.Object)
        {
            error = $"Row {index + 1} of the page is not an object.";
            return false;
        }

        var label = element.TryGetProperty("itemId", out var itemId) && itemId.TryGetInt32(out var number)
            ? $"Item {number.ToString(CultureInfo.InvariantCulture)}"
            : $"Row {index + 1} of the page";

        foreach (var property in element.EnumerateObject())
        {
            if (!SpolDatasetRows.IsProperty(property.Name))
            {
                error = $"{label} has a property Cleanse does not know: {Shorten(property.Name)}.";
                return false;
            }
        }

        try
        {
            row = SpolDatasetRows.Deserialize(element);
            return true;
        }
        catch (JsonException ex)
        {
            var property = ex.Path is { Length: > 2 } path ? path[2..] : "a property";
            error = $"{label}: {Shorten(property)} is not valid. itemId is a number, and every other value is text or null.";
            return false;
        }
    }

    private static string Shorten(string value) => value.Length <= 64 ? value : value[..64] + "…";

    private static IResult Problem(string detail, int status) =>
        TypedResults.Problem(title: "Invalid request", detail: detail, statusCode: status);

    /// <summary>The page as it arrives. Its rows are read one at a time, to say which is at fault.</summary>
    private sealed record PageEnvelope(
        Guid LoadId,
        int Page,
        int PageCount,
        int TotalRows,
        IReadOnlyList<JsonElement>? Rows);
}
