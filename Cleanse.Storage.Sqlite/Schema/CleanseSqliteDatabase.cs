using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Cleanse.Storage.Sqlite.Schema;

/// <summary>
/// Owns the database lifetime and hands out configured connections. For an in-memory database it
/// keeps one connection open for as long as it lives, because SQLite discards an in-memory
/// database the moment the last connection to it closes.
/// </summary>
public sealed class CleanseSqliteDatabase : IAsyncDisposable
{
    private readonly CleanseSqliteOptions _options;
    private readonly string _connectionString;
    private SqliteConnection? _keepAlive;

    public CleanseSqliteDatabase(CleanseSqliteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;
        _connectionString = options.BuildConnectionString();
    }

    internal async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ApplyPragmasAsync(connection, cancellationToken);
        return connection;
    }

    /// <summary>Creates the schema if it is not already there. Safe to call repeatedly.</summary>
    public async Task InitialiseAsync(CancellationToken cancellationToken = default)
    {
        if (_options.InMemory && _keepAlive is null)
        {
            _keepAlive = new SqliteConnection(_connectionString);
            await _keepAlive.OpenAsync(cancellationToken);
            await ApplyPragmasAsync(_keepAlive, cancellationToken);
        }

        await using var connection = await OpenAsync(cancellationToken);

        // Asked before the schema script runs, because the script creates the table.
        var upgradingToHoldings =
            await TableExistsAsync(connection, "issues", cancellationToken) &&
            !await TableExistsAsync(connection, "holdings", cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = ReadSchemaScript();
        await command.ExecuteNonQueryAsync(cancellationToken);
        await UpgradeUserAssignmentAsync(connection, cancellationToken);
        await UpgradeImportScopeAsync(connection, cancellationToken);
        await UpgradeUsersAsync(connection, cancellationToken);
        await UpgradeHoldingWorkflowAsync(connection, upgradingToHoldings, cancellationToken);
        await UpgradeImportDatasetsAsync(connection, cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<HashSet<string>> ColumnsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string table, CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var inspect = connection.CreateCommand();
        inspect.Transaction = transaction;
        inspect.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    /// <summary>
    /// Brings an older database up to the holding workflow, mirroring the HoldingWorkflow
    /// migration: Ignored becomes Can't resolve, and each holding takes one assignee from its
    /// issues. That copy runs once, when the holdings table is new.
    /// </summary>
    private static async Task UpgradeHoldingWorkflowAsync(
        SqliteConnection connection, bool copyAssignments, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var issueColumns = await ColumnsAsync(connection, transaction, "issues", cancellationToken);
        foreach (var (name, definition) in new[]
        {
            ("outcome_reason_id", "TEXT NULL REFERENCES coded_values(id) ON DELETE RESTRICT"),
            ("outcome_reason", "TEXT NULL")
        })
        {
            if (!issueColumns.Contains(name))
            {
                await ExecuteAsync(connection, transaction, $"ALTER TABLE issues ADD COLUMN {name} {definition}", cancellationToken);
            }
        }

        if (!(await ColumnsAsync(connection, transaction, "audit_entries", cancellationToken)).Contains("subject"))
        {
            await ExecuteAsync(connection, transaction, "ALTER TABLE audit_entries ADD COLUMN subject TEXT NULL", cancellationToken);
        }

        await ExecuteAsync(connection, transaction, """
            CREATE INDEX IF NOT EXISTS ix_audit_entries_subject
                ON audit_entries (subject, at_utc DESC, sequence DESC) WHERE subject IS NOT NULL;
            UPDATE issues SET workflow_status = 'nonactionable' WHERE workflow_status = 'ignored';
            """, cancellationToken);

        if (copyAssignments)
        {
            // One made in Cleanse beats one from SPOL, then an open issue's beats a closed one's,
            // then the most recently assigned.
            await using var copy = connection.CreateCommand();
            copy.Transaction = transaction;
            copy.CommandText = """
                INSERT INTO holdings (
                    group_id, cph, assigned_to, assigned_user_id, assigned_user_full_name,
                    assignment_detached_at_utc, created_at_utc, updated_at_utc, version)
                SELECT group_id, cph, assigned_to, assigned_user_id, assigned_user_full_name,
                       assignment_detached_at_utc, $now, $now, 1
                FROM (
                    SELECT i.group_id,
                           COALESCE(json_extract(i.context, '$.cph'), '') AS cph,
                           NULLIF(TRIM(i.assigned_to), '') AS assigned_to,
                           i.assigned_user_id,
                           i.assigned_user_full_name,
                           i.assignment_detached_at_utc,
                           ROW_NUMBER() OVER (
                               PARTITION BY i.group_id, COALESCE(json_extract(i.context, '$.cph'), '')
                               ORDER BY (i.assignment_detached_at_utc IS NOT NULL) DESC,
                                        (i.status = 'active' AND i.workflow_status IN ('todo', 'in-progress')) DESC,
                                        COALESCE(
                                            (SELECT MAX(a.at_utc) FROM audit_entries a
                                             WHERE a.issue_id = i.id AND a.kind = 'IssueAssigned'),
                                            i.updated_at_utc) DESC,
                                        i.id
                           ) AS place
                    FROM issues i
                    WHERE TRIM(COALESCE(json_extract(i.context, '$.cph'), '')) <> ''
                      AND (i.assigned_user_id IS NOT NULL OR TRIM(COALESCE(i.assigned_to, '')) <> '')
                )
                WHERE place = 1
                """;
            copy.Parameters.AddWithValue("$now", SqliteMappings.ToText(DateTimeOffset.UtcNow));
            await copy.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Brings an older import_jobs table up to datasets, mirroring the SpolDatasets migration: each
    /// job can name the dataset it processed, and counts the users it added.
    /// </summary>
    private static async Task UpgradeImportDatasetsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var columns = await ColumnsAsync(connection, transaction, "import_jobs", cancellationToken);
        foreach (var (name, definition) in new[]
        {
            ("dataset_id", "TEXT NULL REFERENCES spol_datasets (id) ON DELETE SET NULL"),
            ("users_created", "INTEGER NOT NULL DEFAULT 0")
        })
        {
            if (!columns.Contains(name))
            {
                await ExecuteAsync(connection, transaction, $"ALTER TABLE import_jobs ADD COLUMN {name} {definition}", cancellationToken);
            }
        }

        await ExecuteAsync(connection, transaction,
            "CREATE INDEX IF NOT EXISTS ix_import_jobs_dataset ON import_jobs (dataset_id) WHERE dataset_id IS NOT NULL",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Serialises writes to the user directory within this process: a sign-in, an admin and an
    /// import must not race to create or claim the same person.
    /// </summary>
    internal SemaphoreSlim DirectoryGate { get; } = new(1, 1);

    private static async Task UpgradeUsersAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using (var inspect = connection.CreateCommand())
        {
            inspect.CommandText = "PRAGMA table_info(users)";
            await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (!columns.Contains("email"))
        {
            // SQLite cannot relax NOT NULL in place, so the older table is rebuilt. Foreign keys go
            // off outside the transaction, as SQLite's documented rebuild procedure requires.
            await ExecuteAsync(connection, null, "PRAGMA foreign_keys = OFF", cancellationToken);
            try
            {
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                await ExecuteAsync(connection, transaction, """
                    CREATE TABLE users_upgraded (
                        id                      TEXT NOT NULL PRIMARY KEY,
                        issuer                  TEXT     NULL,
                        subject                 TEXT     NULL,
                        tenant_id               TEXT     NULL,
                        object_id               TEXT     NULL,
                        client_id               TEXT     NULL,
                        full_name               TEXT NOT NULL,
                        first_logged_in_at_utc  TEXT     NULL,
                        last_logged_in_at_utc   TEXT     NULL,
                        email                   TEXT     NULL
                    );
                    INSERT INTO users_upgraded
                        (id, issuer, subject, tenant_id, object_id, client_id, full_name,
                         first_logged_in_at_utc, last_logged_in_at_utc)
                    SELECT id, issuer, subject, tenant_id, object_id, client_id, full_name,
                           first_logged_in_at_utc, last_logged_in_at_utc
                    FROM users;
                    DROP TABLE users;
                    ALTER TABLE users_upgraded RENAME TO users;
                    """, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            finally
            {
                await ExecuteAsync(connection, null, "PRAGMA foreign_keys = ON", cancellationToken);
            }
        }

        await ExecuteAsync(connection, null, """
            DROP INDEX IF EXISTS ix_users_scope;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_users_identity ON users (issuer, client_id, subject);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_users_email ON users (email);
            CREATE INDEX IF NOT EXISTS ix_users_object ON users (tenant_id, object_id);
            """, cancellationToken);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpgradeImportScopeAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using (var inspect = connection.CreateCommand())
        {
            inspect.CommandText = "PRAGMA table_info(import_jobs)";
            await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        foreach (var column in new[] { "tenant_id", "client_id" })
        {
            if (!columns.Contains(column))
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE import_jobs ADD COLUMN {column} TEXT NULL";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private static async Task UpgradeUserAssignmentAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        // Existing local databases predate stable-user assignment. CREATE TABLE IF NOT EXISTS
        // deliberately leaves those tables alone, so add only the new nullable columns.
        await using var transaction = connection.BeginTransaction();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using (var inspect = connection.CreateCommand())
        {
            inspect.Transaction = transaction;
            inspect.CommandText = "PRAGMA table_info(issues)";
            await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        foreach (var (name, definition) in new[]
        {
            ("assigned_user_id", "TEXT NULL REFERENCES users(id) ON DELETE RESTRICT"),
            ("assigned_user_full_name", "TEXT NULL"),
            ("assignment_detached_at_utc", "TEXT NULL")
        })
        {
            if (!columns.Contains(name))
            {
                await using var alter = connection.CreateCommand();
                alter.Transaction = transaction;
                alter.CommandText = $"ALTER TABLE issues ADD COLUMN {name} {definition}";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task ApplyPragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();

        // Foreign keys are off by default in SQLite and are a per-connection setting, so without
        // this the issue_history constraint silently does nothing.
        command.CommandText = $"PRAGMA foreign_keys = ON; PRAGMA busy_timeout = {_options.BusyTimeoutMilliseconds};";

        if (!_options.InMemory)
        {
            // WAL lets readers run alongside the single writer. Meaningless for in-memory.
            command.CommandText += " PRAGMA journal_mode = WAL;";
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ReadSchemaScript()
    {
        const string resource = "Cleanse.Storage.Sqlite.Schema.schema.sql";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded schema '{resource}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public async ValueTask DisposeAsync()
    {
        DirectoryGate.Dispose();
        if (_keepAlive is not null)
        {
            await _keepAlive.DisposeAsync();
            _keepAlive = null;
        }
    }
}
