using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Storage.Sqlite.Schema;
using Microsoft.Data.Sqlite;

namespace Cleanse.Storage.Sqlite.Stores;

internal sealed class SqliteHoldingStore(CleanseSqliteDatabase database) : IHoldingStore
{
    private const string Columns =
        "group_id, cph, assigned_to, assigned_user_id, assigned_user_full_name, " +
        "assignment_detached_at_utc, created_at_utc, updated_at_utc, version";

    public async Task<Holding?> GetAsync(HoldingKey key, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM holdings WHERE group_id = $group_id AND cph = $cph";
        command.Bind("$group_id", key.GroupId);
        command.Bind("$cph", key.Cph);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<Holding>> GetManyAsync(
        string groupId,
        IReadOnlyCollection<string> cphs,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentNullException.ThrowIfNull(cphs);
        if (cphs.Count == 0)
        {
            return [];
        }

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var names = new List<string>(cphs.Count);
        foreach (var cph in cphs.Distinct(StringComparer.Ordinal))
        {
            var name = $"$cph{names.Count}";
            names.Add(name);
            command.Bind(name, cph);
        }

        command.CommandText =
            $"SELECT {Columns} FROM holdings WHERE group_id = $group_id AND cph IN ({string.Join(", ", names)})";
        command.Bind("$group_id", groupId);

        var found = new List<Holding>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            found.Add(Read(reader));
        }

        return found;
    }

    public async Task<bool> SaveAsync(Holding holding, int expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(holding);
        var snapshot = holding.ToSnapshot();

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        // Version 0 means "not stored yet", so it may only insert; any other version may only
        // update a row still at that version. Either way a lost race changes no rows.
        command.CommandText = expectedVersion == 0
            ? $"""
              INSERT INTO holdings ({Columns})
              VALUES ($group_id, $cph, $assigned_to, $assigned_user_id, $assigned_user_full_name,
                      $detached_at, $created_at, $updated_at, $version)
              ON CONFLICT (group_id, cph) DO NOTHING
              """
            : """
              UPDATE holdings
              SET assigned_to = $assigned_to,
                  assigned_user_id = $assigned_user_id,
                  assigned_user_full_name = $assigned_user_full_name,
                  assignment_detached_at_utc = $detached_at,
                  updated_at_utc = $updated_at,
                  version = $version
              WHERE group_id = $group_id AND cph = $cph AND version = $expected_version
              """;

        command.Bind("$group_id", snapshot.GroupId);
        command.Bind("$cph", snapshot.Cph);
        command.Bind("$assigned_to", snapshot.AssignedTo);
        command.Bind("$assigned_user_id", snapshot.AssignedUserId?.ToString());
        command.Bind("$assigned_user_full_name", snapshot.AssignedUserFullName);
        command.Bind("$detached_at", snapshot.AssignmentDetachedAtUtc?.ToText());
        command.Bind("$updated_at", snapshot.UpdatedAtUtc.ToText());
        command.Bind("$version", snapshot.Version);
        if (expectedVersion == 0)
        {
            command.Bind("$created_at", snapshot.CreatedAtUtc.ToText());
        }
        else
        {
            command.Bind("$expected_version", expectedVersion);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static Holding Read(SqliteDataReader reader) => Holding.FromSnapshot(new HoldingSnapshot(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : SqliteMappings.ToTimestamp(reader.GetString(5)),
        SqliteMappings.ToTimestamp(reader.GetString(6)),
        SqliteMappings.ToTimestamp(reader.GetString(7)),
        reader.GetInt32(8)));
}

internal sealed class SqliteHoldingActivityStore(CleanseSqliteDatabase database) : IHoldingActivityStore
{
    private const string NoteColumns =
        "id, group_id, cph, text, context, created_at_utc, created_by_id, created_by_display_name, created_by_kind";

    private const string ActionColumns =
        "id, batch_id, group_id, cph, action_type_id, action_type_name, created_at_utc, " +
        "created_by_id, created_by_display_name, created_by_kind";

    public async Task AddNoteAsync(HoldingNote note, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(note);

        await using var connection = await database.OpenAsync(cancellationToken);
        await InsertNoteAsync(connection, null, note, cancellationToken);
    }

    public async Task AddActionsAsync(
        IReadOnlyCollection<HoldingAction> actions,
        HoldingNote? note,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actions);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        foreach (var action in actions)
        {
            var snapshot = action.ToSnapshot();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                INSERT INTO holding_actions ({ActionColumns})
                VALUES ($id, $batch_id, $group_id, $cph, $action_type_id, $action_type_name, $created_at_utc,
                        $created_by_id, $created_by_display_name, $created_by_kind)
                """;
            command.Bind("$id", snapshot.Id.ToString());
            command.Bind("$batch_id", snapshot.BatchId.ToString());
            command.Bind("$group_id", snapshot.GroupId);
            command.Bind("$cph", snapshot.Cph);
            command.Bind("$action_type_id", snapshot.ActionTypeId.ToString());
            command.Bind("$action_type_name", snapshot.ActionTypeName);
            BindCreated(command, snapshot.CreatedAtUtc, snapshot.CreatedBy);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (note is not null)
        {
            await InsertNoteAsync(connection, transaction, note, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<HoldingNote>> GetNotesAsync(
        HoldingKey holding,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {NoteColumns} FROM holding_notes
            WHERE group_id = $group_id AND cph = $cph
            ORDER BY created_at_utc DESC, sequence DESC
            LIMIT $limit
            """;
        command.Bind("$group_id", holding.GroupId);
        command.Bind("$cph", holding.Cph);
        command.Bind("$limit", limit);

        var found = new List<HoldingNote>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            found.Add(HoldingNote.FromSnapshot(new HoldingNoteSnapshot(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                SqliteMappings.ToTimestamp(reader.GetString(5)),
                SqliteMappings.RebuildActor(reader.GetString(6), reader.GetString(7), reader.GetString(8)))));
        }

        return found;
    }

    public async Task<IReadOnlyList<HoldingAction>> GetActionsAsync(
        HoldingKey holding,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ActionColumns} FROM holding_actions
            WHERE group_id = $group_id AND cph = $cph
            ORDER BY created_at_utc DESC, sequence DESC
            LIMIT $limit
            """;
        command.Bind("$group_id", holding.GroupId);
        command.Bind("$cph", holding.Cph);
        command.Bind("$limit", limit);

        var found = new List<HoldingAction>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            found.Add(HoldingAction.FromSnapshot(new HoldingActionSnapshot(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                Guid.Parse(reader.GetString(4)),
                reader.GetString(5),
                SqliteMappings.ToTimestamp(reader.GetString(6)),
                SqliteMappings.RebuildActor(reader.GetString(7), reader.GetString(8), reader.GetString(9)))));
        }

        return found;
    }

    private static async Task InsertNoteAsync(
        SqliteConnection connection, SqliteTransaction? transaction, HoldingNote note, CancellationToken cancellationToken)
    {
        var snapshot = note.ToSnapshot();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO holding_notes ({NoteColumns})
            VALUES ($id, $group_id, $cph, $text, $context, $created_at_utc,
                    $created_by_id, $created_by_display_name, $created_by_kind)
            """;
        command.Bind("$id", snapshot.Id.ToString());
        command.Bind("$group_id", snapshot.GroupId);
        command.Bind("$cph", snapshot.Cph);
        command.Bind("$text", snapshot.Text);
        command.Bind("$context", snapshot.Context);
        BindCreated(command, snapshot.CreatedAtUtc, snapshot.CreatedBy);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void BindCreated(SqliteCommand command, DateTimeOffset at, Actor by)
    {
        command.Bind("$created_at_utc", at.ToText());
        command.Bind("$created_by_id", by.Id);
        command.Bind("$created_by_display_name", by.DisplayName);
        command.Bind("$created_by_kind", by.Kind.ToString());
    }
}

internal sealed class SqliteCodedValueStore(CleanseSqliteDatabase database) : ICodedValueStore
{
    private const string Columns = "id, list, name, is_active, created_at_utc, updated_at_utc";

    public async Task<CodedValue?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM coded_values WHERE id = $id";
        command.Bind("$id", id.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<CodedValue>> GetManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return [];
        }

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var names = new List<string>(ids.Count);
        foreach (var id in ids.Distinct())
        {
            var name = $"$id{names.Count}";
            names.Add(name);
            command.Bind(name, id.ToString());
        }

        command.CommandText = $"SELECT {Columns} FROM coded_values WHERE id IN ({string.Join(", ", names)})";

        var found = new List<CodedValue>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            found.Add(Read(reader));
        }

        return found;
    }

    public async Task<IReadOnlyList<CodedValue>> ListAsync(CodedList list, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        // SQLite's default collation is binary, which is the ordinal order the other providers use.
        command.CommandText = $"SELECT {Columns} FROM coded_values WHERE list = $list ORDER BY name_key, id";
        command.Bind("$list", list.ToWire());

        var found = new List<CodedValue>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            found.Add(Read(reader));
        }

        return found;
    }

    public async Task<bool> AddAsync(CodedValue value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        var snapshot = value.ToSnapshot();

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO coded_values ({Columns}, name_key)
            VALUES ($id, $list, $name, $is_active, $created_at_utc, $updated_at_utc, $name_key)
            ON CONFLICT (list, name_key) DO NOTHING
            """;
        Bind(command, snapshot);

        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task SaveAsync(CodedValue value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        var snapshot = value.ToSnapshot();

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO coded_values ({Columns}, name_key)
            VALUES ($id, $list, $name, $is_active, $created_at_utc, $updated_at_utc, $name_key)
            ON CONFLICT (id) DO UPDATE SET
                is_active = excluded.is_active,
                updated_at_utc = excluded.updated_at_utc
            """;
        Bind(command, snapshot);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Bind(SqliteCommand command, CodedValueSnapshot snapshot)
    {
        command.Bind("$id", snapshot.Id.ToString());
        command.Bind("$list", snapshot.List.ToWire());
        command.Bind("$name", snapshot.Name);
        command.Bind("$name_key", CodedValue.KeyOf(snapshot.Name));
        command.Bind("$is_active", snapshot.IsActive ? 1 : 0);
        command.Bind("$created_at_utc", snapshot.CreatedAtUtc.ToText());
        command.Bind("$updated_at_utc", snapshot.UpdatedAtUtc.ToText());
    }

    private static CodedValue Read(SqliteDataReader reader) => CodedValue.FromSnapshot(new CodedValueSnapshot(
        Guid.Parse(reader.GetString(0)),
        CodedLists.TryParse(reader.GetString(1), out var list)
            ? list
            : throw new InvalidOperationException($"'{reader.GetString(1)}' is not a known list."),
        reader.GetString(2),
        reader.GetInt64(3) != 0,
        SqliteMappings.ToTimestamp(reader.GetString(4)),
        SqliteMappings.ToTimestamp(reader.GetString(5))));
}
