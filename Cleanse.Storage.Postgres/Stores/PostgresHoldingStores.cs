using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Storage.Postgres.Schema;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Cleanse.Storage.Postgres.Stores;

internal sealed class PostgresHoldingStore(CleanseDbContext db) : IHoldingStore
{
    private const string Columns = $"""
        {Db.Holding.GroupId}, {Db.Holding.Cph}, {Db.Holding.AssignedTo}, {Db.Holding.AssignedUserId},
        {Db.Holding.AssignedUserFullName}, {Db.Holding.AssignmentDetachedAtUtc}, {Db.Holding.CreatedAtUtc},
        {Db.Holding.UpdatedAtUtc}, {Db.Holding.Version}
        """;

    public async Task<Holding?> GetAsync(HoldingKey key, CancellationToken cancellationToken = default)
    {
        var row = await db.Holdings.AsNoTracking()
            .FirstOrDefaultAsync(r => r.GroupId == key.GroupId && r.Cph == key.Cph, cancellationToken);
        return row?.ToDomain();
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

        var wanted = cphs.Distinct(StringComparer.Ordinal).ToArray();
        var rows = await db.Holdings.AsNoTracking()
            .Where(r => r.GroupId == groupId && wanted.Contains(r.Cph))
            .ToListAsync(cancellationToken);

        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task<bool> SaveAsync(Holding holding, int expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(holding);
        var row = holding.ToSnapshot().ToRow();

        // Version 0 means "not stored yet", so it may only insert; any other version may only
        // update a row still at that version. Either way a lost race affects no rows.
        var sql = expectedVersion == 0
            ? $"""
              INSERT INTO {Db.Holdings} ({Columns})
              VALUES (@group_id, @cph, @assigned_to, @assigned_user_id, @assigned_user_full_name,
                      @detached_at, @created_at, @updated_at, @version)
              ON CONFLICT ({Db.Holding.GroupId}, {Db.Holding.Cph}) DO NOTHING
              """
            : $"""
              UPDATE {Db.Holdings}
              SET {Db.Holding.AssignedTo} = @assigned_to,
                  {Db.Holding.AssignedUserId} = @assigned_user_id,
                  {Db.Holding.AssignedUserFullName} = @assigned_user_full_name,
                  {Db.Holding.AssignmentDetachedAtUtc} = @detached_at,
                  {Db.Holding.UpdatedAtUtc} = @updated_at,
                  {Db.Holding.Version} = @version
              WHERE {Db.Holding.GroupId} = @group_id
                AND {Db.Holding.Cph} = @cph
                AND {Db.Holding.Version} = @expected_version
              """;

        List<object> parameters =
        [
            new NpgsqlParameter("group_id", row.GroupId),
            new NpgsqlParameter("cph", row.Cph),
            new NpgsqlParameter("assigned_to", NpgsqlDbType.Text) { Value = (object?)row.AssignedTo ?? DBNull.Value },
            new NpgsqlParameter("assigned_user_id", NpgsqlDbType.Uuid) { Value = (object?)row.AssignedUserId ?? DBNull.Value },
            new NpgsqlParameter("assigned_user_full_name", NpgsqlDbType.Text)
            {
                Value = (object?)row.AssignedUserFullName ?? DBNull.Value
            },
            new NpgsqlParameter("detached_at", NpgsqlDbType.TimestampTz)
            {
                Value = (object?)row.AssignmentDetachedAtUtc ?? DBNull.Value
            },
            new NpgsqlParameter("updated_at", NpgsqlDbType.TimestampTz) { Value = row.UpdatedAtUtc },
            new NpgsqlParameter("version", row.Version)
        ];

        parameters.Add(expectedVersion == 0
            ? new NpgsqlParameter("created_at", NpgsqlDbType.TimestampTz) { Value = row.CreatedAtUtc }
            : new NpgsqlParameter("expected_version", expectedVersion));

        return await db.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken) > 0;
    }
}

internal sealed class PostgresHoldingActivityStore(CleanseDbContext db) : IHoldingActivityStore
{
    public async Task AddNoteAsync(HoldingNote note, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        db.HoldingNotes.Add(note.ToSnapshot().ToRow());
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddActionsAsync(
        IReadOnlyCollection<HoldingAction> actions,
        HoldingNote? note,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actions);

        // One SaveChanges is one transaction, so the actions and their note land together.
        db.HoldingActions.AddRange(actions.Select(action => action.ToSnapshot().ToRow()));
        if (note is not null)
        {
            db.HoldingNotes.Add(note.ToSnapshot().ToRow());
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<HoldingNote>> GetNotesAsync(
        HoldingKey holding,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var rows = await db.HoldingNotes.AsNoTracking()
            .Where(r => r.GroupId == holding.GroupId && r.Cph == holding.Cph)
            .OrderByDescending(r => r.CreatedAtUtc)
            .ThenByDescending(r => r.Sequence)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task<IReadOnlyList<HoldingAction>> GetActionsAsync(
        HoldingKey holding,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var rows = await db.HoldingActions.AsNoTracking()
            .Where(r => r.GroupId == holding.GroupId && r.Cph == holding.Cph)
            .OrderByDescending(r => r.CreatedAtUtc)
            .ThenByDescending(r => r.Sequence)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(r => r.ToDomain())];
    }
}

internal sealed class PostgresCodedValueStore(CleanseDbContext db) : ICodedValueStore
{
    public async Task<CodedValue?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await db.CodedValues.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        return row?.ToDomain();
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

        var wanted = ids.Distinct().ToArray();
        var rows = await db.CodedValues.AsNoTracking()
            .Where(r => wanted.Contains(r.Id))
            .ToListAsync(cancellationToken);

        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task<IReadOnlyList<CodedValue>> ListAsync(CodedList list, CancellationToken cancellationToken = default)
    {
        var wire = list.ToWire();

        // Ordinal, as the other providers sort, rather than the database's locale.
        var rows = await db.CodedValues.AsNoTracking()
            .Where(r => r.List == wire)
            .OrderBy(r => EF.Functions.Collate(r.NameKey, "C"))
            .ToListAsync(cancellationToken);

        return [.. rows.Select(r => r.ToDomain())];
    }

    public async Task<bool> AddAsync(CodedValue value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        db.CodedValues.Add(value.ToSnapshot().ToRow());

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // ux_coded_values_list_name: the list already has an entry by this name.
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task SaveAsync(CodedValue value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);

        var row = value.ToSnapshot().ToRow();
        var existing = await db.CodedValues.FirstOrDefaultAsync(r => r.Id == row.Id, cancellationToken);
        if (existing is null)
        {
            db.CodedValues.Add(row);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(row);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
