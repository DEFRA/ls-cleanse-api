using Cleanse.Core.Abstractions;
using Cleanse.Core.Commands.Datasets;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain.Datasets;

namespace Cleanse.Core.Queries.Datasets;

/// <summary>
/// One dataset. With a <see cref="Key"/>, as the connector asks, an unknown dataset and a wrong
/// key get the same answer; without one, as an admin asks, an unknown dataset is not found.
/// </summary>
public sealed record GetSpolDatasetQuery(Guid DatasetId, string? Key = null) : IQuery<SpolDatasetDto>;

internal sealed class GetSpolDatasetQueryHandler(ISpolDatasetStore datasets, IImportJobStore imports)
    : IQueryHandler<GetSpolDatasetQuery, SpolDatasetDto>
{
    public async Task<Result<SpolDatasetDto>> HandleAsync(
        GetSpolDatasetQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var dataset = await datasets.GetAsync(query.DatasetId, cancellationToken);
        if (query.Key is not null && !SpolDatasetKey.Matches(dataset?.KeyHash, query.Key))
        {
            return DomainErrors.DatasetUnauthorised();
        }

        if (dataset is null)
        {
            return DomainErrors.DatasetNotFound(query.DatasetId);
        }

        return dataset.ToDto(await SpolDatasetReads.LastProcessingAsync(imports, dataset.Id, cancellationToken));
    }
}

/// <summary>Newest first.</summary>
public sealed record SearchSpolDatasetsQuery(PageRequest? Page = null) : IQuery<PagedResult<SpolDatasetDto>>;

internal sealed class SearchSpolDatasetsQueryHandler(ISpolDatasetStore datasets, IImportJobStore imports)
    : IQueryHandler<SearchSpolDatasetsQuery, PagedResult<SpolDatasetDto>>
{
    public async Task<Result<PagedResult<SpolDatasetDto>>> HandleAsync(
        SearchSpolDatasetsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var page = await datasets.SearchAsync(query.Page ?? PageRequest.Default, cancellationToken);
        var items = new List<SpolDatasetDto>(page.Items.Count);

        // Admins keep a handful of datasets, so one small query each beats a batch store method.
        foreach (var dataset in page.Items)
        {
            items.Add(dataset.ToDto(await SpolDatasetReads.LastProcessingAsync(imports, dataset.Id, cancellationToken)));
        }

        return new PagedResult<SpolDatasetDto>(items, page.PageNumber, page.PageSize, page.TotalCount);
    }
}
