using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Storage.Sqlite.Schema;
using Microsoft.Data.Sqlite;

namespace Cleanse.Storage.Sqlite.Stores;

internal sealed class SqliteDataResetStore(CleanseSqliteDatabase database) : IDataResetStore
{
    public async Task<ResetCounts> ResetAsync(
        string? groupId,
        string actorId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        async Task<int> DeleteAsync(string everything, string scopedToGroup)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = groupId is null ? everything : scopedToGroup;

            if (groupId is not null)
            {
                command.Bind("$group", groupId);
            }

            return await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var rejections = await DeleteAsync(
            "DELETE FROM import_job_rejections",
            """
            DELETE FROM import_job_rejections
            WHERE import_job_id IN (SELECT id FROM import_jobs WHERE group_id = $group)
            """);

        var importJobs = await DeleteAsync(
            "DELETE FROM import_jobs",
            "DELETE FROM import_jobs WHERE group_id = $group");

        var history = await DeleteAsync(
            "DELETE FROM issue_history",
            """
            DELETE FROM issue_history
            WHERE issue_id IN (SELECT id FROM issues WHERE group_id = $group)
            """);

        // Has to precede both tables it reads: audit rows reach their group through them. The
        // admin lists and settings outlive a reset, so their audit does too.
        const string codedValue = AuditSubjects.CodedValuePrefix;
        const string setting = AuditSubjects.SettingPrefix;
        const string holding = AuditSubjects.HoldingPrefix;
        var audit = await DeleteAsync(
            $"""
            DELETE FROM audit_entries
            WHERE subject IS NULL
               OR (substr(subject, 1, {codedValue.Length}) <> '{codedValue}'
                   AND substr(subject, 1, {setting.Length}) <> '{setting}')
            """,
            $"""
            DELETE FROM audit_entries
            WHERE issue_id IN (SELECT id FROM issues WHERE group_id = $group)
               OR run_id IN (SELECT run_id FROM rule_runs WHERE group_id = $group)
               OR substr(subject, 1, length('{holding}' || $group || ':')) = '{holding}' || $group || ':'
            """);

        var issues = await DeleteAsync(
            "DELETE FROM issues",
            "DELETE FROM issues WHERE group_id = $group");

        var runs = await DeleteAsync(
            "DELETE FROM rule_runs",
            "DELETE FROM rule_runs WHERE group_id = $group");

        var notes = await DeleteAsync(
            "DELETE FROM holding_notes",
            "DELETE FROM holding_notes WHERE group_id = $group");

        var actions = await DeleteAsync(
            "DELETE FROM holding_actions",
            "DELETE FROM holding_actions WHERE group_id = $group");

        var holdings = await DeleteAsync(
            "DELETE FROM holdings",
            "DELETE FROM holdings WHERE group_id = $group");

        // The dashboard's summary is built from what has just gone, so it goes too.
        foreach (var table in new[] { "mi_events", "mi_daily_changes", "mi_issue_states", "mi_holding_states", "mi_refresh" })
        {
            await DeleteAsync($"DELETE FROM {table}", $"DELETE FROM {table} WHERE group_id = $group");
        }

        await using (var record = connection.CreateCommand())
        {
            record.Transaction = transaction;
            record.CommandText = """
                INSERT INTO data_resets (at_utc, scope, actor_id, issues)
                VALUES ($at, $scope, $actor, $issues)
                """;
            record.Bind("$at", at.ToText());
            record.Bind("$scope", groupId ?? "all");
            record.Bind("$actor", actorId);
            record.Bind("$issues", issues);
            await record.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new ResetCounts(issues, history, audit, runs, importJobs, rejections, holdings, notes, actions);
    }

    public async Task<DataResetRecord?> GetLastResetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT at_utc, scope, actor_id, issues
            FROM data_resets
            ORDER BY sequence DESC
            LIMIT 1
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new DataResetRecord(
                SqliteMappings.ToTimestamp(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3))
            : null;
    }
}
