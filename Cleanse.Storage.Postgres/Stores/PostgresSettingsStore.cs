using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Storage.Postgres.Schema;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cleanse.Storage.Postgres.Stores;

internal sealed class PostgresSettingsStore(CleanseDbContext db) : ISettingsStore
{
    public async Task<ServiceSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        var row = await db.ServiceSettings.AsNoTracking().FirstOrDefaultAsync(r => r.Id == 1, cancellationToken);
        return row is null ? null : new ServiceSettings(row.MinimumIssueAgeDays);
    }

    /// <summary>An upsert, so two admins saving the first value at once cannot collide on the key.</summary>
    public async Task SaveAsync(ServiceSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await db.Database.ExecuteSqlRawAsync(
            $"""
             INSERT INTO {Db.ServiceSettings} (id, minimum_issue_age_days)
             VALUES (1, @minimum_issue_age_days)
             ON CONFLICT (id) DO UPDATE SET minimum_issue_age_days = EXCLUDED.minimum_issue_age_days
             """,
            [new NpgsqlParameter("minimum_issue_age_days", settings.MinimumIssueAgeDays)],
            cancellationToken);
    }
}
