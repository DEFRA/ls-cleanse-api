using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Storage.Postgres.Schema;
using Microsoft.EntityFrameworkCore;

namespace Cleanse.Storage.Postgres.Stores;

internal sealed class PostgresUserStore(CleanseDbContext db) : IUserStore
{
    public async Task<KnownUser> UpsertLoginAsync(
        KnownUser login, IReadOnlyCollection<string> claimEmails, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(login);
        ArgumentNullException.ThrowIfNull(claimEmails);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockDirectoryAsync(cancellationToken);

        var existing = await db.Users.AsNoTracking().FirstOrDefaultAsync(
                u => u.Issuer == login.Issuer && u.ClientId == login.ClientId && u.Subject == login.Subject,
                cancellationToken)
            ?? await db.Users.AsNoTracking()
                .Where(u => u.Subject != null && u.TenantId == login.TenantId && u.ObjectId == login.ObjectId)
                .OrderBy(u => u.FirstLoggedInAtUtc).ThenBy(u => u.Id)
                .FirstOrDefaultAsync(cancellationToken)
            ?? await FindUnclaimedAsync(claimEmails, cancellationToken);

        List<UserRow> saved;
        if (existing is null)
        {
            var email = await IsFreeAsync(login.Email, cancellationToken) ? login.Email : null;
            saved = await db.Users.FromSqlInterpolated($"""
                INSERT INTO users
                    (id, issuer, subject, tenant_id, object_id, client_id, full_name,
                     first_logged_in_at_utc, last_logged_in_at_utc, email)
                VALUES
                    ({login.Id}, {login.Issuer}, {login.Subject}, {login.TenantId}, {login.ObjectId},
                     {login.ClientId}, {login.FullName}, {login.FirstLoggedInAtUtc}, {login.LastLoggedInAtUtc}, {email})
                RETURNING *
                """).AsNoTracking().ToListAsync(cancellationToken);
        }
        else
        {
            var email = existing.Email ?? (await IsFreeAsync(login.Email, cancellationToken) ? login.Email : null);
            saved = await db.Users.FromSqlInterpolated($"""
                UPDATE users SET
                    issuer = {login.Issuer},
                    subject = {login.Subject},
                    tenant_id = {login.TenantId},
                    object_id = {login.ObjectId},
                    client_id = {login.ClientId},
                    full_name = {login.FullName},
                    email = {email},
                    first_logged_in_at_utc = COALESCE(first_logged_in_at_utc, {login.FirstLoggedInAtUtc}),
                    last_logged_in_at_utc = GREATEST(last_logged_in_at_utc, {login.LastLoggedInAtUtc})
                WHERE id = {existing.Id}
                RETURNING *
                """).AsNoTracking().ToListAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return saved.Single().ToDomain();
    }

    public async Task<IReadOnlyList<UserRegistration>> RegisterAsync(
        IReadOnlyCollection<KnownUser> users, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(users);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockDirectoryAsync(cancellationToken);

        var emails = users.Select(u => u.Email).OfType<string>().ToArray();
        var known = (await db.Users.AsNoTracking()
                .Where(u => u.Email != null && emails.Contains(u.Email))
                .ToListAsync(cancellationToken))
            .ToDictionary(u => u.Email!, StringComparer.Ordinal);

        var results = new List<UserRegistration>(users.Count);
        foreach (var user in users)
        {
            if (user.Email is not null && known.TryGetValue(user.Email, out var row))
            {
                results.Add(new UserRegistration(row.ToDomain(), false));
                continue;
            }

            var inserted = (await db.Users.FromSqlInterpolated($"""
                INSERT INTO users (id, full_name, email)
                VALUES ({user.Id}, {user.FullName}, {user.Email})
                RETURNING *
                """).AsNoTracking().ToListAsync(cancellationToken)).Single();
            if (inserted.Email is not null)
            {
                known[inserted.Email] = inserted;
            }

            results.Add(new UserRegistration(inserted.ToDomain(), true));
        }

        await transaction.CommitAsync(cancellationToken);
        return results;
    }

    public async Task<KnownUser?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        return row?.ToDomain();
    }

    public async Task<PagedResult<KnownUser>> SearchAsync(PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var count = await db.Users.LongCountAsync(cancellationToken);
        var rows = await db.Users.AsNoTracking()
            .OrderBy(u => u.FullName.ToLower()).ThenBy(u => u.Id)
            .Skip(page.Skip).Take(page.PageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<KnownUser>([.. rows.Select(u => u.ToDomain())], page.PageNumber, page.PageSize, count);
    }

    /// <summary>
    /// Serialises every write to the directory, so a sign-in, an admin and an import cannot race to
    /// create or claim the same person. Released when the transaction ends.
    /// </summary>
    private Task LockDirectoryAsync(CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended('cleanse.users', 0))", cancellationToken);

    private async Task<UserRow?> FindUnclaimedAsync(
        IReadOnlyCollection<string> emails, CancellationToken cancellationToken)
    {
        foreach (var email in emails)
        {
            var row = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Subject == null && u.Email == email, cancellationToken);
            if (row is not null)
            {
                return row;
            }
        }

        return null;
    }

    private async Task<bool> IsFreeAsync(string? email, CancellationToken cancellationToken) =>
        email is not null && !await db.Users.AnyAsync(u => u.Email == email, cancellationToken);
}
