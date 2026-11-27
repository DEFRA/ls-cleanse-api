using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain.Datasets;

namespace Cleanse.Storage.InMemory;

public sealed class InMemorySpolDatasetStore : ISpolDatasetStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, SpolDatasetSnapshot> _datasets = [];
    private readonly Dictionary<Guid, SortedDictionary<int, string>> _items = [];

    public Task<SpolDataset?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_datasets.TryGetValue(id, out var snapshot) ? SpolDataset.FromSnapshot(snapshot) : null);
        }
    }

    public Task<PagedResult<SpolDataset>> SearchAsync(PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var items = _datasets.Values
                .OrderByDescending(d => d.CreatedAtUtc)
                .ThenByDescending(d => d.Id)
                .Skip(page.Skip)
                .Take(page.PageSize)
                .Select(SpolDataset.FromSnapshot)
                .ToList();

            return Task.FromResult(new PagedResult<SpolDataset>(items, page.PageNumber, page.PageSize, _datasets.Count));
        }
    }

    public Task CreateAsync(SpolDataset dataset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_datasets.TryAdd(dataset.Id, dataset.ToSnapshot()))
            {
                throw new InvalidOperationException($"Dataset '{dataset.Id}' already exists.");
            }

            _items[dataset.Id] = [];
        }

        return Task.CompletedTask;
    }

    public Task<bool> SaveAsync(SpolDataset dataset, int expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_datasets.TryGetValue(dataset.Id, out var stored) || stored.Version != expectedVersion)
            {
                return Task.FromResult(false);
            }

            _datasets[dataset.Id] = dataset.ToSnapshot();
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _items.Remove(id);
            return Task.FromResult(_datasets.Remove(id));
        }
    }

    public Task<SpolDatasetPageOutcome> ApplyPageAsync(
        SpolDataset dataset,
        int expectedVersion,
        bool clearRows,
        IReadOnlyCollection<SpolDatasetItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(items);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_datasets.TryGetValue(dataset.Id, out var stored) || stored.Version != expectedVersion)
            {
                return Task.FromResult(SpolDatasetPageOutcome.VersionConflict);
            }

            // Worked on a copy, so a page that is refused leaves the rows as they were.
            var rows = clearRows ? new SortedDictionary<int, string>() : new SortedDictionary<int, string>(_items[dataset.Id]);
            foreach (var item in items)
            {
                rows[item.ItemId] = item.Data;
            }

            if (rows.Count > dataset.ExpectedRows)
            {
                return Task.FromResult(SpolDatasetPageOutcome.TooManyRows);
            }

            dataset.RecordRowCount(rows.Count);
            _items[dataset.Id] = rows;
            _datasets[dataset.Id] = dataset.ToSnapshot();
            return Task.FromResult(SpolDatasetPageOutcome.Stored);
        }
    }

    public Task<IReadOnlyList<SpolDatasetItem>> ReadItemsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<SpolDatasetItem> items = _items.TryGetValue(id, out var rows)
                ? [.. rows.Select(row => new SpolDatasetItem(row.Key, row.Value))]
                : [];
            return Task.FromResult(items);
        }
    }
}
