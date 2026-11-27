using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain.Imports;
using Cleanse.Storage.Sqlite.Schema;
using Microsoft.Data.Sqlite;

namespace Cleanse.Storage.Sqlite.Stores;

internal sealed class SqliteImportJobStore(CleanseSqliteDatabase database) : IImportJobStore
{
    private const string Columns =
        "id, group_id, file_name, blob_key, status, total_rows, processed_rows, matched, " +
        "unmatched, duplicates_collapsed, invalid, requested_by, created_at_utc, started_at_utc, " +
        "finished_at_utc, heartbeat_at_utc, failure_reason, version, tenant_id, client_id, dataset_id, users_created";

    public async Task<ImportJob?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM import_jobs WHERE id = $id";
        command.Bind("$id", id.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<bool> TryCreateAsync(ImportJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO import_jobs ({Columns})
            VALUES ($id, $group_id, $file_name, $blob_key, $status, $total_rows, $processed_rows,
                    $matched, $unmatched, $duplicates_collapsed, $invalid, $requested_by,
                    $created_at_utc, $started_at_utc, $finished_at_utc, $heartbeat_at_utc,
                    $failure_reason, $version, $tenant_id, $client_id, $dataset_id, $users_created)
            """;
        Bind(command, job.ToSnapshot());

        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // The unique partial index rejected a second active job for this group.
            return false;
        }
    }

    public async Task SaveAsync(ImportJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE import_jobs SET
                status = $status,
                total_rows = $total_rows,
                processed_rows = $processed_rows,
                matched = $matched,
                unmatched = $unmatched,
                duplicates_collapsed = $duplicates_collapsed,
                invalid = $invalid,
                users_created = $users_created,
                started_at_utc = $started_at_utc,
                finished_at_utc = $finished_at_utc,
                heartbeat_at_utc = $heartbeat_at_utc,
                failure_reason = $failure_reason,
                version = $version
            WHERE id = $id
            """;
        Bind(command, job.ToSnapshot());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ImportJob?> GetActiveForGroupAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns} FROM import_jobs
            WHERE group_id = $group_id AND status IN ('Pending', 'Processing')
            ORDER BY created_at_utc DESC LIMIT 1
            """;
        command.Bind("$group_id", groupId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<bool> HasActiveForDatasetAsync(Guid datasetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM import_jobs
                WHERE dataset_id = $dataset_id AND status IN ('Pending', 'Processing'))
            """;
        command.Bind("$dataset_id", datasetId.ToString());
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    public async Task<ImportJob?> LeaseNextPendingAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);

        // One statement so two workers cannot both claim the same row. RETURNING gives us the
        // leased job without a second round trip.
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE import_jobs
            SET status = 'Processing', started_at_utc = $now, heartbeat_at_utc = $now,
                version = version + 1
            WHERE id = (
                SELECT id FROM import_jobs WHERE status = 'Pending'
                ORDER BY created_at_utc ASC LIMIT 1
            )
            RETURNING {Columns}
            """;
        command.Bind("$now", now.ToText());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<ImportJob>> FindStalledAsync(
        DateTimeOffset olderThan,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns} FROM import_jobs
            WHERE status = 'Processing'
              AND heartbeat_at_utc IS NOT NULL
              AND heartbeat_at_utc < $older_than
            ORDER BY heartbeat_at_utc ASC
            LIMIT $limit
            """;
        command.Bind("$older_than", olderThan.ToText());
        command.Bind("$limit", limit);

        var jobs = new List<ImportJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(Read(reader));
        }

        return jobs;
    }

    public async Task<PagedResult<ImportJob>> SearchAsync(
        string? groupId,
        PageRequest page,
        Guid? datasetId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        var conditions = new List<string>();
        if (groupId is not null)
        {
            conditions.Add("group_id = $group_id");
        }

        if (datasetId is not null)
        {
            conditions.Add("dataset_id = $dataset_id");
        }

        var filter = conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions);
        await using var connection = await database.OpenAsync(cancellationToken);

        void BindFilter(SqliteCommand command)
        {
            if (groupId is not null)
            {
                command.Bind("$group_id", groupId);
            }

            if (datasetId is not null)
            {
                command.Bind("$dataset_id", datasetId.Value.ToString());
            }
        }

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM import_jobs {filter}";
        BindFilter(countCommand);

        var total = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken));

        await using var pageCommand = connection.CreateCommand();
        pageCommand.CommandText = $"""
            SELECT {Columns} FROM import_jobs
            {filter}
            ORDER BY created_at_utc DESC, id DESC
            LIMIT $limit OFFSET $offset
            """;
        BindFilter(pageCommand);

        pageCommand.Bind("$limit", page.PageSize);
        pageCommand.Bind("$offset", page.Skip);

        var items = new List<ImportJob>(page.PageSize);
        await using var reader = await pageCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(Read(reader));
        }

        return new PagedResult<ImportJob>(items, page.PageNumber, page.PageSize, total);
    }

    public async Task AddRejectionsAsync(
        IReadOnlyCollection<ImportJobRejection> rejections,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rejections);
        if (rejections.Count == 0)
        {
            return;
        }

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO import_job_rejections (import_job_id, row_number, issue_id, reason, detail)
            VALUES ($import_job_id, $row_number, $issue_id, $reason, $detail)
            """;

        foreach (var rejection in rejections)
        {
            command.Parameters.Clear();
            command.Bind("$import_job_id", rejection.ImportJobId.ToString());
            command.Bind("$row_number", rejection.RowNumber);
            command.Bind("$issue_id", rejection.IssueId);
            command.Bind("$reason", rejection.Reason);
            command.Bind("$detail", rejection.Detail);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<PagedResult<ImportJobRejection>> GetRejectionsAsync(
        Guid importJobId,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        await using var connection = await database.OpenAsync(cancellationToken);

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText =
            "SELECT COUNT(*) FROM import_job_rejections WHERE import_job_id = $id";
        countCommand.Bind("$id", importJobId.ToString());
        var total = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken));

        await using var pageCommand = connection.CreateCommand();
        pageCommand.CommandText = """
            SELECT import_job_id, row_number, issue_id, reason, detail
            FROM import_job_rejections
            WHERE import_job_id = $id
            ORDER BY row_number ASC, sequence ASC
            LIMIT $limit OFFSET $offset
            """;
        pageCommand.Bind("$id", importJobId.ToString());
        pageCommand.Bind("$limit", page.PageSize);
        pageCommand.Bind("$offset", page.Skip);

        var items = new List<ImportJobRejection>(page.PageSize);
        await using var reader = await pageCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ImportJobRejection(
                Guid.Parse(reader.GetString(0)),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return new PagedResult<ImportJobRejection>(items, page.PageNumber, page.PageSize, total);
    }

    private static ImportJob Read(SqliteDataReader reader) => ImportJob.FromSnapshot(new ImportJobSnapshot(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3) is { Length: > 0 } blobKey ? blobKey : null,
        Enum.Parse<ImportJobStatus>(reader.GetString(4)),
        new ImportJobCounters(
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt32(21)),
        reader.GetString(11),
        SqliteMappings.ToTimestamp(reader.GetString(12)),
        reader.IsDBNull(13) ? null : SqliteMappings.ToTimestamp(reader.GetString(13)),
        reader.IsDBNull(14) ? null : SqliteMappings.ToTimestamp(reader.GetString(14)),
        reader.IsDBNull(15) ? null : SqliteMappings.ToTimestamp(reader.GetString(15)),
        reader.IsDBNull(16) ? null : reader.GetString(16),
        reader.GetInt32(17),
        reader.IsDBNull(18) ? null : reader.GetString(18),
        reader.IsDBNull(19) ? null : reader.GetString(19),
        reader.IsDBNull(20) ? null : Guid.Parse(reader.GetString(20))));

    private static void Bind(SqliteCommand command, ImportJobSnapshot snapshot)
    {
        command.Bind("$id", snapshot.Id.ToString());
        command.Bind("$tenant_id", snapshot.TenantId);
        command.Bind("$client_id", snapshot.ClientId);
        command.Bind("$group_id", snapshot.GroupId);
        command.Bind("$file_name", snapshot.FileName);
        command.Bind("$blob_key", snapshot.BlobKey ?? string.Empty);
        command.Bind("$dataset_id", snapshot.DatasetId?.ToString());
        command.Bind("$users_created", snapshot.Counters.UsersCreated);
        command.Bind("$status", snapshot.Status.ToString());
        command.Bind("$total_rows", snapshot.Counters.TotalRows);
        command.Bind("$processed_rows", snapshot.Counters.ProcessedRows);
        command.Bind("$matched", snapshot.Counters.Matched);
        command.Bind("$unmatched", snapshot.Counters.Unmatched);
        command.Bind("$duplicates_collapsed", snapshot.Counters.DuplicatesCollapsed);
        command.Bind("$invalid", snapshot.Counters.Invalid);
        command.Bind("$requested_by", snapshot.RequestedBy);
        command.Bind("$created_at_utc", snapshot.CreatedAtUtc.ToText());
        command.Bind("$started_at_utc", snapshot.StartedAtUtc?.ToText());
        command.Bind("$finished_at_utc", snapshot.FinishedAtUtc?.ToText());
        command.Bind("$heartbeat_at_utc", snapshot.HeartbeatAtUtc?.ToText());
        command.Bind("$failure_reason", snapshot.FailureReason);
        command.Bind("$version", snapshot.Version);
    }
}

/// <summary>
/// Local development file store. Keys are service-generated, so a user-supplied filename can
/// never influence the path.
/// </summary>
public sealed class FileSystemImportFileStore(string rootPath) : IImportFileStore
{
    public async Task WriteAsync(string key, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(content);

        var path = ResolvePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var file = File.Create(path);
        await content.CopyToAsync(file, cancellationToken);
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<Stream>(File.OpenRead(ResolvePath(key)));
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(ResolvePath(key)));
    }

    private string ResolvePath(string key)
    {
        var root = Path.GetFullPath(rootPath);
        var path = Path.GetFullPath(Path.Combine(root, key));

        // Defence in depth: keys are generated, but a traversal must never escape the root.
        if (!path.StartsWith(root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Import file key '{key}' resolves outside the store root.");
        }

        return path;
    }
}
