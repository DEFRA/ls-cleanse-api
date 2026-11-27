using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain.Datasets;
using Cleanse.Storage.Postgres.Schema;
using Microsoft.EntityFrameworkCore;

namespace Cleanse.Storage.Postgres.Stores;

internal sealed class PostgresSpolDatasetStore(CleanseDbContext db) : ISpolDatasetStore
{
    public async Task<SpolDataset?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await db.SpolDatasets.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public async Task<PagedResult<SpolDataset>> SearchAsync(PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        var total = await db.SpolDatasets.LongCountAsync(cancellationToken);
        var rows = await db.SpolDatasets.AsNoTracking()
            .OrderByDescending(r => r.CreatedAtUtc)
            .ThenByDescending(r => r.Id)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<SpolDataset>([.. rows.Select(ToDomain)], page.PageNumber, page.PageSize, total);
    }

    public async Task CreateAsync(SpolDataset dataset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        var row = ToRow(dataset.ToSnapshot());
        db.SpolDatasets.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        db.Entry(row).State = EntityState.Detached;
    }

    public async Task<bool> SaveAsync(SpolDataset dataset, int expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        return await UpdateAsync(dataset.ToSnapshot(), expectedVersion, cancellationToken) > 0;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        // The rows go with it by the cascade, and its import jobs lose the link but stay.
        await db.SpolDatasets.Where(r => r.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;

    public async Task<SpolDatasetPageOutcome> ApplyPageAsync(
        SpolDataset dataset,
        int expectedVersion,
        bool clearRows,
        IReadOnlyCollection<SpolDatasetItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(items);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Takes the dataset's row lock, so pages for one dataset are stored one at a time. Table
        // names are written out because every hole in these interpolated strings is a parameter.
        var locked = await db.Database.ExecuteSqlAsync($"""
            UPDATE spol_datasets SET version = version
            WHERE id = {dataset.Id} AND version = {expectedVersion}
            """, cancellationToken);
        if (locked == 0)
        {
            return SpolDatasetPageOutcome.VersionConflict;
        }

        if (clearRows)
        {
            await db.Database.ExecuteSqlAsync(
                $"DELETE FROM spol_dataset_items WHERE dataset_id = {dataset.Id}", cancellationToken);
        }

        var itemIds = items.Select(item => item.ItemId).ToArray();
        var data = items.Select(item => item.Data).ToArray();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO spol_dataset_items (dataset_id, item_id, data)
            SELECT {dataset.Id}, item.item_id, item.data::jsonb
            FROM unnest({itemIds}, {data}) AS item (item_id, data)
            ON CONFLICT (dataset_id, item_id) DO UPDATE SET data = EXCLUDED.data
            """, cancellationToken);

        var rowCount = await db.SpolDatasetItems.CountAsync(r => r.DatasetId == dataset.Id, cancellationToken);
        if (rowCount > dataset.ExpectedRows)
        {
            return SpolDatasetPageOutcome.TooManyRows;
        }

        dataset.RecordRowCount(rowCount);
        await UpdateAsync(dataset.ToSnapshot(), expectedVersion, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return SpolDatasetPageOutcome.Stored;
    }

    public async Task<IReadOnlyList<SpolDatasetItem>> ReadItemsAsync(Guid id, CancellationToken cancellationToken = default) =>
        await db.SpolDatasetItems.AsNoTracking()
            .Where(r => r.DatasetId == id)
            .OrderBy(r => r.ItemId)
            .Select(r => new SpolDatasetItem(r.ItemId, r.Data))
            .ToListAsync(cancellationToken);

    private Task<int> UpdateAsync(SpolDatasetSnapshot snapshot, int expectedVersion, CancellationToken cancellationToken) =>
        db.SpolDatasets
            .Where(r => r.Id == snapshot.Id && r.Version == expectedVersion)
            .ExecuteUpdateAsync(set => set
                .SetProperty(r => r.Name, snapshot.Name)
                .SetProperty(r => r.Status, snapshot.Status.ToString())
                .SetProperty(r => r.RowCount, snapshot.RowCount)
                .SetProperty(r => r.ExpectedRows, snapshot.ExpectedRows)
                .SetProperty(r => r.PageCount, snapshot.PageCount)
                .SetProperty(r => r.CurrentLoadId, snapshot.CurrentLoadId)
                .SetProperty(r => r.LoadStartedAtUtc, snapshot.LoadStartedAtUtc)
                .SetProperty(r => r.LastLoadedAtUtc, snapshot.LastLoadedAtUtc)
                .SetProperty(r => r.UpdatedAtUtc, snapshot.UpdatedAtUtc)
                .SetProperty(r => r.Version, snapshot.Version), cancellationToken);

    private static SpolDataset ToDomain(SpolDatasetRow row) => SpolDataset.FromSnapshot(new SpolDatasetSnapshot(
        row.Id,
        row.Name,
        row.KeyHash,
        Enum.Parse<SpolDatasetStatus>(row.Status),
        row.RowCount,
        row.ExpectedRows,
        row.PageCount,
        row.CurrentLoadId,
        row.LoadStartedAtUtc,
        row.LastLoadedAtUtc,
        row.CreatedById,
        row.CreatedByName,
        row.CreatedAtUtc,
        row.UpdatedAtUtc,
        row.Version));

    private static SpolDatasetRow ToRow(SpolDatasetSnapshot snapshot) => new()
    {
        Id = snapshot.Id,
        Name = snapshot.Name,
        KeyHash = snapshot.KeyHash,
        Status = snapshot.Status.ToString(),
        RowCount = snapshot.RowCount,
        ExpectedRows = snapshot.ExpectedRows,
        PageCount = snapshot.PageCount,
        CurrentLoadId = snapshot.CurrentLoadId,
        LoadStartedAtUtc = snapshot.LoadStartedAtUtc,
        LastLoadedAtUtc = snapshot.LastLoadedAtUtc,
        CreatedById = snapshot.CreatedById,
        CreatedByName = snapshot.CreatedByName,
        CreatedAtUtc = snapshot.CreatedAtUtc,
        UpdatedAtUtc = snapshot.UpdatedAtUtc,
        Version = snapshot.Version
    };
}
