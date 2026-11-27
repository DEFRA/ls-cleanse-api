using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Storage.Sqlite.Schema;

namespace Cleanse.Storage.Sqlite.Stores;

internal sealed class SqliteSettingsStore(CleanseSqliteDatabase database) : ISettingsStore
{
    public async Task<ServiceSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT minimum_issue_age_days FROM service_settings WHERE id = 1";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new ServiceSettings(reader.GetInt32(0)) : null;
    }

    public async Task SaveAsync(ServiceSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO service_settings (id, minimum_issue_age_days)
            VALUES (1, $minimum_issue_age_days)
            ON CONFLICT (id) DO UPDATE SET minimum_issue_age_days = excluded.minimum_issue_age_days
            """;
        command.Bind("$minimum_issue_age_days", settings.MinimumIssueAgeDays);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
