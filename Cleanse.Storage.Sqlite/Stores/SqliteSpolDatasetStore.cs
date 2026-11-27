using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain.Datasets;
using Cleanse.Storage.Sqlite.Schema;
using Microsoft.Data.Sqlite;

namespace Cleanse.Storage.Sqlite.Stores;

internal sealed class SqliteSpolDatasetStore(CleanseSqliteDatabase database) : ISpolDatasetStore
{
    private const string Columns =
        "id, name, key_hash, status, row_count, expected_rows, page_count, current_load_id, " +
        "load_started_at_utc, last_loaded_at_utc, created_by_id, created_by_name, created_at_utc, " +
        "updated_at_utc, version";

    public async Task<SpolDataset?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM spol_datasets WHERE id = $id";
        command.Bind("$id", id.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<PagedResult<SpolDataset>> SearchAsync(PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM spol_datasets";
        var total = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken));

        await using var pageCommand = connection.CreateCommand();
        pageCommand.CommandText = $"""
            SELECT {Columns} FROM spol_datasets
            ORDER BY created_at_utc DESC, id DESC
            LIMIT $limit OFFSET $offset
            """;
        pageCommand.Bind("$limit", page.PageSize);
        pageCommand.Bind("$offset", page.Skip);

        var items = new List<SpolDataset>(page.PageSize);
        await using var reader = await pageCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(Read(reader));
        }

        return new PagedResult<SpolDataset>(items, page.PageNumber, page.PageSize, total);
    }

    public async Task CreateAsync(SpolDataset dataset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO spol_datasets ({Columns})
            VALUES ($id, $name, $key_hash, $status, $row_count, $expected_rows, $page_count, $current_load_id,
                    $load_started_at_utc, $last_loaded_at_utc, $created_by_id, $created_by_name, $created_at_utc,
                    $updated_at_utc, $version)
            """;
        Bind(command, dataset.ToSnapshot());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> SaveAsync(SpolDataset dataset, int expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        await using var connection = await database.OpenAsync(cancellationToken);
        return await UpdateAsync(connection, null, dataset, expectedVersion, cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        // The rows go with it, by the foreign key's cascade.
        command.CommandText = "DELETE FROM spol_datasets WHERE id = $id";
        command.Bind("$id", id.ToString());
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<SpolDatasetPageOutcome> ApplyPageAsync(
        SpolDataset dataset,
        int expectedVersion,
        bool clearRows,
        IReadOnlyCollection<SpolDatasetItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(items);

        await using var connection = await database.OpenAsync(cancellationToken);

        // Immediate, so the version read below cannot change before this page is stored.
        await using var transaction = connection.BeginTransaction(deferred: false);

        await using (var version = connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = "SELECT version FROM spol_datasets WHERE id = $id";
            version.Bind("$id", dataset.Id.ToString());
            if (await version.ExecuteScalarAsync(cancellationToken) is not long stored || stored != expectedVersion)
            {
                return SpolDatasetPageOutcome.VersionConflict;
            }
        }

        if (clearRows)
        {
            await using var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM spol_dataset_items WHERE dataset_id = $id";
            clear.Bind("$id", dataset.Id.ToString());
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO spol_dataset_items (dataset_id, item_id, data)
                VALUES ($dataset_id, $item_id, $data)
                ON CONFLICT (dataset_id, item_id) DO UPDATE SET data = excluded.data
                """;
            var datasetId = upsert.Parameters.Add("$dataset_id", SqliteType.Text);
            var itemId = upsert.Parameters.Add("$item_id", SqliteType.Integer);
            var data = upsert.Parameters.Add("$data", SqliteType.Text);
            datasetId.Value = dataset.Id.ToString();

            foreach (var item in items)
            {
                itemId.Value = item.ItemId;
                data.Value = item.Data;
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        int rowCount;
        await using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM spol_dataset_items WHERE dataset_id = $id";
            count.Bind("$id", dataset.Id.ToString());
            rowCount = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
        }

        if (rowCount > dataset.ExpectedRows)
        {
            return SpolDatasetPageOutcome.TooManyRows;
        }

        dataset.RecordRowCount(rowCount);
        await UpdateAsync(connection, transaction, dataset, expectedVersion, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return SpolDatasetPageOutcome.Stored;
    }

    public async Task<IReadOnlyList<SpolDatasetItem>> ReadItemsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT item_id, data FROM spol_dataset_items WHERE dataset_id = $id ORDER BY item_id";
        command.Bind("$id", id.ToString());

        var items = new List<SpolDatasetItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new SpolDatasetItem(reader.GetInt32(0), reader.GetString(1)));
        }

        return items;
    }

    private static async Task<bool> UpdateAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        SpolDataset dataset,
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE spol_datasets SET
                name = $name,
                status = $status,
                row_count = $row_count,
                expected_rows = $expected_rows,
                page_count = $page_count,
                current_load_id = $current_load_id,
                load_started_at_utc = $load_started_at_utc,
                last_loaded_at_utc = $last_loaded_at_utc,
                updated_at_utc = $updated_at_utc,
                version = $version
            WHERE id = $id AND version = $expected_version
            """;
        Bind(command, dataset.ToSnapshot());
        command.Bind("$expected_version", expectedVersion);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static SpolDataset Read(SqliteDataReader reader) => SpolDataset.FromSnapshot(new SpolDatasetSnapshot(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        Enum.Parse<SpolDatasetStatus>(reader.GetString(3)),
        reader.GetInt32(4),
        reader.IsDBNull(5) ? null : reader.GetInt32(5),
        reader.IsDBNull(6) ? null : reader.GetInt32(6),
        reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)),
        reader.IsDBNull(8) ? null : SqliteMappings.ToTimestamp(reader.GetString(8)),
        reader.IsDBNull(9) ? null : SqliteMappings.ToTimestamp(reader.GetString(9)),
        reader.GetString(10),
        reader.GetString(11),
        SqliteMappings.ToTimestamp(reader.GetString(12)),
        SqliteMappings.ToTimestamp(reader.GetString(13)),
        reader.GetInt32(14)));

    private static void Bind(SqliteCommand command, SpolDatasetSnapshot snapshot)
    {
        command.Bind("$id", snapshot.Id.ToString());
        command.Bind("$name", snapshot.Name);
        command.Bind("$key_hash", snapshot.KeyHash);
        command.Bind("$status", snapshot.Status.ToString());
        command.Bind("$row_count", snapshot.RowCount);
        command.Bind("$expected_rows", snapshot.ExpectedRows);
        command.Bind("$page_count", snapshot.PageCount);
        command.Bind("$current_load_id", snapshot.CurrentLoadId?.ToString());
        command.Bind("$load_started_at_utc", snapshot.LoadStartedAtUtc?.ToText());
        command.Bind("$last_loaded_at_utc", snapshot.LastLoadedAtUtc?.ToText());
        command.Bind("$created_by_id", snapshot.CreatedById);
        command.Bind("$created_by_name", snapshot.CreatedByName);
        command.Bind("$created_at_utc", snapshot.CreatedAtUtc.ToText());
        command.Bind("$updated_at_utc", snapshot.UpdatedAtUtc.ToText());
        command.Bind("$version", snapshot.Version);
    }
}
