using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Storage.Sqlite.Schema;
using Microsoft.Data.Sqlite;

namespace Cleanse.Storage.Sqlite.Stores;

internal sealed class SqliteUserStore(CleanseSqliteDatabase database) : IUserStore
{
    private const string Columns =
        "id, issuer, subject, tenant_id, object_id, client_id, full_name, " +
        "first_logged_in_at_utc, last_logged_in_at_utc, email";

    public async Task<KnownUser> UpsertLoginAsync(
        KnownUser login, IReadOnlyCollection<string> claimEmails, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(login);
        ArgumentNullException.ThrowIfNull(claimEmails);
        await database.DirectoryGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            var existing = await FindAsync(connection, transaction,
                    "issuer = $issuer AND client_id = $client AND subject = $subject",
                    command =>
                    {
                        command.Bind("$issuer", login.Issuer);
                        command.Bind("$client", login.ClientId);
                        command.Bind("$subject", login.Subject);
                    }, cancellationToken)
                ?? await FindAsync(connection, transaction,
                    "subject IS NOT NULL AND tenant_id = $tenant AND object_id = $object " +
                    "ORDER BY first_logged_in_at_utc, id",
                    command =>
                    {
                        command.Bind("$tenant", login.TenantId);
                        command.Bind("$object", login.ObjectId);
                    }, cancellationToken)
                ?? await FindUnclaimedAsync(connection, transaction, claimEmails, cancellationToken);

            await using var write = connection.CreateCommand();
            write.Transaction = transaction;
            if (existing is null)
            {
                write.CommandText = $"""
                    INSERT INTO users ({Columns})
                    VALUES ($id, $issuer, $subject, $tenant, $object, $client, $name, $first, $last, $email)
                    RETURNING {Columns}
                    """;
                write.Bind("$id", login.Id.ToString());
                write.Bind("$email",
                    await IsFreeAsync(connection, transaction, login.Email, cancellationToken) ? login.Email : null);
            }
            else
            {
                // SQLite's MAX is NULL when either side is, so the later login is chosen explicitly.
                write.CommandText = $"""
                    UPDATE users SET
                        issuer = $issuer, subject = $subject, tenant_id = $tenant, object_id = $object,
                        client_id = $client, full_name = $name, email = $email,
                        first_logged_in_at_utc = COALESCE(first_logged_in_at_utc, $first),
                        last_logged_in_at_utc = CASE
                            WHEN last_logged_in_at_utc IS NULL OR last_logged_in_at_utc < $last THEN $last
                            ELSE last_logged_in_at_utc END
                    WHERE id = $id
                    RETURNING {Columns}
                    """;
                write.Bind("$id", existing.Id.ToString());
                write.Bind("$email", existing.Email ??
                    (await IsFreeAsync(connection, transaction, login.Email, cancellationToken) ? login.Email : null));
            }

            write.Bind("$issuer", login.Issuer);
            write.Bind("$subject", login.Subject);
            write.Bind("$tenant", login.TenantId);
            write.Bind("$object", login.ObjectId);
            write.Bind("$client", login.ClientId);
            write.Bind("$name", login.FullName);
            write.Bind("$first", login.FirstLoggedInAtUtc?.ToText());
            write.Bind("$last", login.LastLoggedInAtUtc?.ToText());

            KnownUser saved;
            await using (var reader = await write.ExecuteReaderAsync(cancellationToken))
            {
                await reader.ReadAsync(cancellationToken);
                saved = Read(reader);
            }

            await transaction.CommitAsync(cancellationToken);
            return saved;
        }
        finally
        {
            database.DirectoryGate.Release();
        }
    }

    public async Task<IReadOnlyList<UserRegistration>> RegisterAsync(
        IReadOnlyCollection<KnownUser> users, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(users);
        await database.DirectoryGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var results = new List<UserRegistration>(users.Count);
            foreach (var user in users)
            {
                var known = user.Email is null
                    ? null
                    : await FindAsync(connection, transaction, "email = $email",
                        command => command.Bind("$email", user.Email), cancellationToken);
                if (known is not null)
                {
                    results.Add(new UserRegistration(known, false));
                    continue;
                }

                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO users (id, full_name, email) VALUES ($id, $name, $email)";
                insert.Bind("$id", user.Id.ToString());
                insert.Bind("$name", user.FullName);
                insert.Bind("$email", user.Email);
                await insert.ExecuteNonQueryAsync(cancellationToken);
                results.Add(new UserRegistration(user, true));
            }

            await transaction.CommitAsync(cancellationToken);
            return results;
        }
        finally
        {
            database.DirectoryGate.Release();
        }
    }

    public async Task<KnownUser?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM users WHERE id = $id";
        command.Bind("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<PagedResult<KnownUser>> SearchAsync(PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM users";
        var total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns} FROM users
            ORDER BY full_name COLLATE NOCASE, id LIMIT $limit OFFSET $offset
            """;
        command.Bind("$limit", page.PageSize);
        command.Bind("$offset", page.Skip);
        var items = new List<KnownUser>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(Read(reader));
        }

        return new PagedResult<KnownUser>(items, page.PageNumber, page.PageSize, total);
    }

    private static async Task<KnownUser?> FindAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string where,
        Action<SqliteCommand> bind,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Columns} FROM users WHERE {where} LIMIT 1";
        bind(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static async Task<KnownUser?> FindUnclaimedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<string> emails,
        CancellationToken cancellationToken)
    {
        foreach (var email in emails)
        {
            var user = await FindAsync(connection, transaction, "subject IS NULL AND email = $email",
                command => command.Bind("$email", email), cancellationToken);
            if (user is not null)
            {
                return user;
            }
        }

        return null;
    }

    private static async Task<bool> IsFreeAsync(
        SqliteConnection connection, SqliteTransaction transaction, string? email, CancellationToken cancellationToken) =>
        email is not null &&
        await FindAsync(connection, transaction, "email = $email",
            command => command.Bind("$email", email), cancellationToken) is null;

    private static KnownUser Read(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.IsDBNull(1) ? null : reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetString(6),
        reader.IsDBNull(7) ? null : SqliteMappings.ToTimestamp(reader.GetString(7)),
        reader.IsDBNull(8) ? null : SqliteMappings.ToTimestamp(reader.GetString(8)),
        reader.IsDBNull(9) ? null : reader.GetString(9));
}
