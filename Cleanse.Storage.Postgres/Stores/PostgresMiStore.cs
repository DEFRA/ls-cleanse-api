using System.Data;
using System.Text;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Mi;
using Cleanse.Storage.Postgres.Schema;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Cleanse.Storage.Postgres.Stores;

/// <summary>
/// The dashboard's summary in Postgres. The history is read in one repeatable-read snapshot, and a
/// replace deletes and copies the whole summary in one transaction under an advisory lock, so two
/// instances refreshing at once cannot interleave their rows.
/// </summary>
internal sealed class PostgresMiStore(CleanseDbContext db) : IMiStore
{
    // Notes, actions and audit entries are only ever added, but their numbers are taken when they
    // are written, not when they commit, so the count is there to see one that commits late.
    // Every change to an issue or holding moves its version. The audit part spans every group,
    // which can only cause a refresh that finds nothing new.
    private static readonly string FingerprintSql = $"""
        SELECT concat_ws('|',
            (SELECT count(*) || ':' || coalesce(sum(version), 0) FROM {Db.Issues} WHERE group_id = @group),
            (SELECT count(*) || ':' || coalesce(max(sequence), 0) FROM {Db.AuditEntries} WHERE {Db.MiAuditFilter}),
            (SELECT count(*) || ':' || coalesce(max(sequence), 0) FROM {Db.HoldingNotes} WHERE group_id = @group),
            (SELECT count(*) || ':' || coalesce(max(sequence), 0) FROM {Db.HoldingActions} WHERE group_id = @group),
            (SELECT count(*) || ':' || coalesce(sum(version), 0) FROM {Db.Holdings} WHERE group_id = @group),
            (SELECT coalesce(min(started_at_utc)::text, '') FROM {Db.RuleRuns} WHERE group_id = @group))
        """;

    private const string HoldingColumns =
        "group_id, cph, status, has_active_issue, open_issues, oldest_open_raised_at_utc, oldest_open_raised_day, " +
        "in_progress_since_utc, last_worked_at_utc, assigned_user_id, assignee_name";

    private delegate MiEventCount ReadDimension(MiEventCount count, NpgsqlDataReader reader, int ordinal);

    /// <summary>What each dimension groups by. Only these column names ever reach the SQL.</summary>
    private static readonly (MiEventDimensions Flag, string Column, ReadDimension Read)[] Dimensions =
    [
        (MiEventDimensions.Day, "day", (c, r, i) => c with { Day = Value<DateOnly>(r, i) }),
        (MiEventDimensions.Unit, "unit", (c, r, i) => c with { Unit = Parse(r, i, MiNames.ParseUnit) }),
        (MiEventDimensions.Kind, "kind", (c, r, i) => c with { Kind = Parse(r, i, MiNames.ParseKind) }),
        (MiEventDimensions.Source, "source", (c, r, i) => c with { Source = Parse(r, i, MiNames.ParseSource) }),
        (MiEventDimensions.User, "user_id", (c, r, i) => c with { UserId = Value<Guid>(r, i) }),
        (MiEventDimensions.Rule, "rule_id", (c, r, i) => c with { RuleId = Text(r, i) }),
        (MiEventDimensions.Cph, "cph", (c, r, i) => c with { Cph = Text(r, i) }),
        (MiEventDimensions.FromStatus, "from_status", (c, r, i) => c with { FromStatus = Text(r, i) }),
        (MiEventDimensions.ToStatus, "to_status", (c, r, i) => c with { ToStatus = Text(r, i) }),
        (MiEventDimensions.FromWorkflow, "from_workflow", (c, r, i) => c with { FromWorkflow = Text(r, i) }),
        (MiEventDimensions.ToWorkflow, "to_workflow", (c, r, i) => c with { ToWorkflow = Text(r, i) }),
        (MiEventDimensions.Reason, "reason_id", (c, r, i) => c with { ReasonId = Value<Guid>(r, i) }),
        (MiEventDimensions.Reason, "reason_name", (c, r, i) => c with { ReasonName = Text(r, i) }),
        (MiEventDimensions.OpenChange, "open_change", (c, r, i) => c with { OpenChange = Parse(r, i, MiNames.ParseOpenChange) }),
        (MiEventDimensions.OpenDelta, "open_delta", (c, r, i) => c with { OpenDelta = Value<int>(r, i) }),
        (MiEventDimensions.ElapsedDays, "elapsed_days", (c, r, i) => c with { ElapsedDays = Value<int>(r, i) }),
        (MiEventDimensions.InactiveNow, "inactive_now", (c, r, i) => c with { InactiveNow = Value<bool>(r, i) }),
        (MiEventDimensions.ActionsSinceClosed, "actions_since_closed", (c, r, i) => c with { ActionsSinceClosed = Value<int>(r, i) })
    ];

    public async Task<string> GetFingerprintAsync(string groupId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        var connection = await OpenConnectionAsync(cancellationToken);
        return await FingerprintAsync(connection, null, groupId, cancellationToken);
    }

    public async Task<MiHistory> ReadHistoryAsync(string groupId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

        var fingerprint = await FingerprintAsync(connection, transaction, groupId, cancellationToken);

        var issues = new List<MiHistoryIssue>();
        await ReadAsync(connection, transaction, groupId, $"""
            SELECT id, rule_id, context ->> 'cph', status, workflow_status, outcome_reason_id, outcome_reason,
                   created_at_utc, deactivated_at_utc, updated_at_utc
            FROM {Db.Issues}
            WHERE group_id = @group
            """, reader =>
        {
            var cph = Text(reader, 2);
            issues.Add(new MiHistoryIssue(
                reader.GetString(0),
                reader.GetString(1),
                string.IsNullOrWhiteSpace(cph) ? null : cph,
                RowMappings.ParseIssueStatus(reader.GetString(3)),
                RowMappings.ParseWorkflowStatus(reader.GetString(4)),
                Value<Guid>(reader, 5),
                Text(reader, 6),
                reader.GetFieldValue<DateTimeOffset>(7),
                Value<DateTimeOffset>(reader, 8),
                reader.GetFieldValue<DateTimeOffset>(9)));
        }, cancellationToken);

        var facts = new List<MiAuditFact>();
        await ReadAsync(connection, transaction, groupId, $"""
            SELECT sequence, id, issue_id, kind, actor_id, actor_display_name, actor_kind, at_utc, changes::text
            FROM {Db.AuditEntries}
            WHERE {Db.MiAuditFilter}
              AND issue_id IN (SELECT id FROM {Db.Issues} WHERE group_id = @group)
            """, reader =>
        {
            var entry = new AuditEntryRow
            {
                Id = reader.GetGuid(1),
                IssueId = reader.GetString(2),
                Kind = reader.GetString(3),
                ActorId = reader.GetString(4),
                ActorDisplayName = reader.GetString(5),
                ActorKind = reader.GetString(6),
                AtUtc = reader.GetFieldValue<DateTimeOffset>(7),
                Changes = reader.GetString(8)
            }.ToDomain();
            if (MiAuditFact.From(entry, reader.GetInt64(0)) is { } fact)
            {
                facts.Add(fact);
            }
        }, cancellationToken);

        var holdings = new List<MiHistoryHolding>();
        await ReadAsync(connection, transaction, groupId, $"""
            SELECT cph, assigned_user_id, assigned_to, assigned_user_full_name
            FROM {Db.Holdings}
            WHERE group_id = @group
            """, reader => holdings.Add(new MiHistoryHolding(
                reader.GetString(0), Value<Guid>(reader, 1), Text(reader, 2), Text(reader, 3))),
            cancellationToken);

        var activity = new List<MiHistoryActivity>();
        await ReadAsync(connection, transaction, groupId, $"""
            SELECT sequence, cph, created_at_utc, created_by_id, created_by_display_name, created_by_kind,
                   action_type_id, action_type_name
            FROM {Db.HoldingActions}
            WHERE group_id = @group
            """, reader => activity.Add(new MiHistoryActivity(
                MiUnit.Action,
                reader.GetString(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetInt64(0),
                RowMappings.RebuildActor(reader.GetString(3), reader.GetString(4), reader.GetString(5)),
                reader.GetGuid(6),
                reader.GetString(7))),
            cancellationToken);

        await ReadAsync(connection, transaction, groupId, $"""
            SELECT sequence, cph, created_at_utc, created_by_id, created_by_display_name, created_by_kind
            FROM {Db.HoldingNotes}
            WHERE group_id = @group
            """, reader => activity.Add(new MiHistoryActivity(
                MiUnit.Note,
                reader.GetString(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetInt64(0),
                RowMappings.RebuildActor(reader.GetString(3), reader.GetString(4), reader.GetString(5)))),
            cancellationToken);

        DateTimeOffset? firstRun = null;
        await ReadAsync(connection, transaction, groupId,
            $"SELECT min(started_at_utc) FROM {Db.RuleRuns} WHERE group_id = @group",
            reader => firstRun = Value<DateTimeOffset>(reader, 0),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new MiHistory(groupId, fingerprint, issues, facts, holdings, activity, firstRun);
    }

    public async Task<bool> ReplaceAsync(MiSummary summary, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var groupId = summary.Refresh.GroupId;

        var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Held to the end of the transaction. Without it, two replaces could each delete before
        // either inserted, and both sets of rows would be kept.
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_try_advisory_xact_lock(hashtextextended(@key, 0))", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("key", $"cleanse.mi:{groupId}");
            if (await lockCommand.ExecuteScalarAsync(cancellationToken) is not true)
            {
                return false;
            }
        }

        foreach (var table in Db.MiTables)
        {
            await using var delete = new NpgsqlCommand($"DELETE FROM {table} WHERE group_id = @group", connection, transaction);
            delete.Parameters.AddWithValue("group", groupId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var writer = await connection.BeginBinaryImportAsync($"""
            COPY {Db.MiEvents} (group_id, sequence, at_utc, day, unit, kind, source, issue_id, rule_id, cph, user_id,
                user_name, from_status, to_status, from_workflow, to_workflow, reason_id, reason_name, open_change,
                open_delta, elapsed_days, inactive_now, actions_since_closed)
            FROM STDIN (FORMAT BINARY)
            """, cancellationToken))
        {
            foreach (var e in summary.Events)
            {
                await writer.StartRowAsync(cancellationToken);
                await TextAsync(writer, e.GroupId, cancellationToken);
                await writer.WriteAsync(e.Sequence, NpgsqlDbType.Bigint, cancellationToken);
                await writer.WriteAsync(e.AtUtc.ToUniversalTime(), NpgsqlDbType.TimestampTz, cancellationToken);
                await writer.WriteAsync(e.Day, NpgsqlDbType.Date, cancellationToken);
                await TextAsync(writer, e.Unit.ToWire(), cancellationToken);
                await TextAsync(writer, e.Kind.ToWire(), cancellationToken);
                await TextAsync(writer, e.Source.ToWire(), cancellationToken);
                await TextAsync(writer, e.IssueId, cancellationToken);
                await TextAsync(writer, e.RuleId, cancellationToken);
                await TextAsync(writer, e.Cph, cancellationToken);
                await ValueAsync(writer, e.UserId, NpgsqlDbType.Uuid, cancellationToken);
                await TextAsync(writer, e.UserName, cancellationToken);
                await TextAsync(writer, e.FromStatus, cancellationToken);
                await TextAsync(writer, e.ToStatus, cancellationToken);
                await TextAsync(writer, e.FromWorkflow, cancellationToken);
                await TextAsync(writer, e.ToWorkflow, cancellationToken);
                await ValueAsync(writer, e.ReasonId, NpgsqlDbType.Uuid, cancellationToken);
                await TextAsync(writer, e.ReasonName, cancellationToken);
                await TextAsync(writer, e.OpenChange?.ToWire(), cancellationToken);
                await writer.WriteAsync(e.OpenDelta, NpgsqlDbType.Integer, cancellationToken);
                await ValueAsync(writer, e.ElapsedDays, NpgsqlDbType.Integer, cancellationToken);
                await ValueAsync(writer, e.InactiveNow, NpgsqlDbType.Boolean, cancellationToken);
                await ValueAsync(writer, e.ActionsSinceClosed, NpgsqlDbType.Integer, cancellationToken);
            }

            await writer.CompleteAsync(cancellationToken);
        }

        await using (var writer = await connection.BeginBinaryImportAsync(
            $"COPY {Db.MiDailyChanges} (group_id, unit, day, rule_id, status, state, delta) FROM STDIN (FORMAT BINARY)",
            cancellationToken))
        {
            foreach (var change in summary.DailyChanges)
            {
                await writer.StartRowAsync(cancellationToken);
                await TextAsync(writer, change.GroupId, cancellationToken);
                await TextAsync(writer, change.Unit.ToWire(), cancellationToken);
                await writer.WriteAsync(change.Day, NpgsqlDbType.Date, cancellationToken);
                await TextAsync(writer, change.RuleId, cancellationToken);
                await TextAsync(writer, change.Status, cancellationToken);
                await TextAsync(writer, change.State, cancellationToken);
                await writer.WriteAsync(change.Delta, NpgsqlDbType.Integer, cancellationToken);
            }

            await writer.CompleteAsync(cancellationToken);
        }

        await using (var writer = await connection.BeginBinaryImportAsync($"""
            COPY {Db.MiIssueStates} (group_id, issue_id, rule_id, cph, status, workflow_status, reason_id, reason_name,
                first_raised_at_utc, first_raised_day, last_changed_at_utc)
            FROM STDIN (FORMAT BINARY)
            """, cancellationToken))
        {
            foreach (var issue in summary.Issues)
            {
                await writer.StartRowAsync(cancellationToken);
                await TextAsync(writer, issue.GroupId, cancellationToken);
                await TextAsync(writer, issue.IssueId, cancellationToken);
                await TextAsync(writer, issue.RuleId, cancellationToken);
                await TextAsync(writer, issue.Cph, cancellationToken);
                await TextAsync(writer, issue.Status.ToWire(), cancellationToken);
                await TextAsync(writer, issue.WorkflowStatus.ToWire(), cancellationToken);
                await ValueAsync(writer, issue.ReasonId, NpgsqlDbType.Uuid, cancellationToken);
                await TextAsync(writer, issue.ReasonName, cancellationToken);
                await writer.WriteAsync(issue.FirstRaisedAtUtc.ToUniversalTime(), NpgsqlDbType.TimestampTz, cancellationToken);
                await writer.WriteAsync(issue.FirstRaisedDay, NpgsqlDbType.Date, cancellationToken);
                await writer.WriteAsync(issue.LastChangedAtUtc.ToUniversalTime(), NpgsqlDbType.TimestampTz, cancellationToken);
            }

            await writer.CompleteAsync(cancellationToken);
        }

        await using (var writer = await connection.BeginBinaryImportAsync(
            $"COPY {Db.MiHoldingStates} ({HoldingColumns}) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            foreach (var holding in summary.Holdings)
            {
                await writer.StartRowAsync(cancellationToken);
                await TextAsync(writer, holding.GroupId, cancellationToken);
                await TextAsync(writer, holding.Cph, cancellationToken);
                await TextAsync(writer, holding.Status.ToWire(), cancellationToken);
                await writer.WriteAsync(holding.HasActiveIssue, NpgsqlDbType.Boolean, cancellationToken);
                await writer.WriteAsync(holding.OpenIssues, NpgsqlDbType.Integer, cancellationToken);
                await ValueAsync(writer, holding.OldestOpenRaisedAtUtc?.ToUniversalTime(), NpgsqlDbType.TimestampTz, cancellationToken);
                await ValueAsync(writer, holding.OldestOpenRaisedDay, NpgsqlDbType.Date, cancellationToken);
                await ValueAsync(writer, holding.InProgressSinceUtc?.ToUniversalTime(), NpgsqlDbType.TimestampTz, cancellationToken);
                await ValueAsync(writer, holding.LastWorkedAtUtc?.ToUniversalTime(), NpgsqlDbType.TimestampTz, cancellationToken);
                await ValueAsync(writer, holding.AssignedUserId, NpgsqlDbType.Uuid, cancellationToken);
                await TextAsync(writer, holding.AssigneeName, cancellationToken);
            }

            await writer.CompleteAsync(cancellationToken);
        }

        var refresh = summary.Refresh;
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {Db.MiRefresh} (group_id, built_at_utc, checked_at_utc, fingerprint, version,
                duration_milliseconds, history_from_utc, issues, events, mismatches)
            VALUES (@group, @built, @checked, @fingerprint, @version, @duration, @history_from, @issues, @events, @mismatches)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("group", refresh.GroupId);
            insert.Parameters.Add(Timestamp("built", refresh.BuiltAtUtc));
            insert.Parameters.Add(Timestamp("checked", refresh.CheckedAtUtc));
            insert.Parameters.AddWithValue("fingerprint", refresh.Fingerprint);
            insert.Parameters.AddWithValue("version", refresh.Version);
            insert.Parameters.AddWithValue("duration", refresh.DurationMilliseconds);
            insert.Parameters.Add(Timestamp("history_from", refresh.HistoryFromUtc));
            insert.Parameters.AddWithValue("issues", refresh.Issues);
            insert.Parameters.AddWithValue("events", refresh.Events);
            insert.Parameters.AddWithValue("mismatches", refresh.Mismatches);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<MiRefreshRecord?> GetRefreshAsync(string groupId, CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        MiRefreshRecord? found = null;
        await ReadAsync(connection, null, groupId, $"""
            SELECT group_id, built_at_utc, checked_at_utc, fingerprint, version, duration_milliseconds,
                   history_from_utc, issues, events, mismatches
            FROM {Db.MiRefresh}
            WHERE group_id = @group
            """, reader => found = new MiRefreshRecord(
                reader.GetString(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                Value<DateTimeOffset>(reader, 6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetInt32(9)),
            cancellationToken);
        return found;
    }

    public async Task MarkCheckedAsync(string groupId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"UPDATE {Db.MiRefresh} SET checked_at_utc = @at WHERE group_id = @group", connection);
        command.Parameters.AddWithValue("group", groupId);
        command.Parameters.Add(Timestamp("at", at));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MiEventCount>> CountEventsAsync(
        MiEventFilter filter,
        MiEventDimensions by,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand { Connection = connection };

        var selected = Dimensions.Where(dimension => by.HasFlag(dimension.Flag)).ToList();
        var columns = string.Join(", ", selected.Select(dimension => dimension.Column));
        var sql = new StringBuilder("SELECT count(*)::int, max(at_utc)");
        if (selected.Count > 0)
        {
            sql.Append(", ").Append(columns);
        }

        sql.Append($" FROM {Db.MiEvents} WHERE group_id = @group AND day >= @from AND day <= @to");
        command.Parameters.AddWithValue("group", filter.GroupId);
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Date) { Value = filter.FromDay });
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Date) { Value = filter.ToDay });

        void Any<T>(string column, IReadOnlyCollection<T>? values, Func<T, string> toWire)
        {
            if (values is null)
            {
                return;
            }

            sql.Append($" AND {column} = ANY(@{column})");
            command.Parameters.Add(new NpgsqlParameter(column, NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = values.Select(toWire).ToArray()
            });
        }

        Any("unit", filter.Units, MiNames.ToWire);
        Any("kind", filter.Kinds, MiNames.ToWire);
        Any("source", filter.Sources, MiNames.ToWire);
        if (filter.RuleId is not null)
        {
            sql.Append(" AND rule_id = @rule");
            command.Parameters.AddWithValue("rule", filter.RuleId);
        }

        if (filter.UserId is { } user)
        {
            sql.Append(" AND user_id = @user");
            command.Parameters.AddWithValue("user", user);
        }

        sql.Append(selected.Count > 0 ? $" GROUP BY {columns}" : " HAVING count(*) > 0");
        command.CommandText = sql.ToString();

        var counts = new List<MiEventCount>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var count = new MiEventCount(reader.GetInt32(0), reader.GetFieldValue<DateTimeOffset>(1));
            for (var i = 0; i < selected.Count; i++)
            {
                count = selected[i].Read(count, reader, i + 2);
            }

            counts.Add(count);
        }

        return counts;
    }

    public async Task<IReadOnlyList<MiDailyChange>> GetDailyChangesAsync(
        string groupId,
        MiUnit unit,
        string? ruleId,
        DateOnly toDay,
        CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT day, rule_id, status, state, delta
            FROM {Db.MiDailyChanges}
            WHERE group_id = @group AND unit = @unit AND day <= @to AND (@rule::text IS NULL OR rule_id = @rule)
            """, connection);
        command.Parameters.AddWithValue("group", groupId);
        command.Parameters.AddWithValue("unit", unit.ToWire());
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Date) { Value = toDay });
        command.Parameters.Add(new NpgsqlParameter("rule", NpgsqlDbType.Text) { Value = (object?)ruleId ?? DBNull.Value });

        var changes = new List<MiDailyChange>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            changes.Add(new MiDailyChange(
                groupId, reader.GetFieldValue<DateOnly>(0), unit, reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt32(4)));
        }

        return changes;
    }

    public async Task<IReadOnlyList<MiIssueStateCount>> CountIssueStatesAsync(
        string groupId,
        string? ruleId,
        CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT rule_id, status, workflow_status, first_raised_day, count(*)::int
            FROM {Db.MiIssueStates}
            WHERE group_id = @group AND (@rule::text IS NULL OR rule_id = @rule)
            GROUP BY rule_id, status, workflow_status, first_raised_day
            """, connection);
        command.Parameters.AddWithValue("group", groupId);
        command.Parameters.Add(new NpgsqlParameter("rule", NpgsqlDbType.Text) { Value = (object?)ruleId ?? DBNull.Value });

        var counts = new List<MiIssueStateCount>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            counts.Add(new MiIssueStateCount(
                reader.GetString(0),
                RowMappings.ParseIssueStatus(reader.GetString(1)),
                RowMappings.ParseWorkflowStatus(reader.GetString(2)),
                reader.GetFieldValue<DateOnly>(3),
                reader.GetInt32(4)));
        }

        return counts;
    }

    public async Task<IReadOnlyList<MiHoldingStateCount>> CountHoldingStatesAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        var counts = new List<MiHoldingStateCount>();
        await ReadAsync(connection, null, groupId, $"""
            SELECT status, has_active_issue, assigned_user_id, oldest_open_raised_day, count(*)::int
            FROM {Db.MiHoldingStates}
            WHERE group_id = @group
            GROUP BY status, has_active_issue, assigned_user_id, oldest_open_raised_day
            """, reader => counts.Add(new MiHoldingStateCount(
                ParseHoldingStatus(reader.GetString(0)),
                reader.GetBoolean(1),
                Value<Guid>(reader, 2),
                Value<DateOnly>(reader, 3),
                reader.GetInt32(4))),
            cancellationToken);
        return counts;
    }

    public async Task<IReadOnlyList<MiHoldingState>> ListHoldingStatesAsync(
        MiHoldingListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.List == MiHoldingList.Stalled && query.Before is null)
        {
            return [];
        }

        var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(query.List == MiHoldingList.Stalled
            ? $"""
              SELECT {HoldingColumns} FROM {Db.MiHoldingStates}
              WHERE group_id = @group AND status = @in_progress
                AND in_progress_since_utc <= @before
                AND (last_worked_at_utc IS NULL OR last_worked_at_utc <= @before)
              ORDER BY coalesce(last_worked_at_utc, in_progress_since_utc), cph COLLATE "C"
              LIMIT @limit
              """
            : $"""
              SELECT {HoldingColumns} FROM {Db.MiHoldingStates}
              WHERE group_id = @group AND status IN (@todo, @in_progress) AND oldest_open_raised_at_utc IS NOT NULL
              ORDER BY oldest_open_raised_at_utc, cph COLLATE "C"
              LIMIT @limit
              """, connection);
        command.Parameters.AddWithValue("group", query.GroupId);
        command.Parameters.AddWithValue("todo", HoldingStatus.Todo.ToWire());
        command.Parameters.AddWithValue("in_progress", HoldingStatus.InProgress.ToWire());
        command.Parameters.AddWithValue("limit", query.Limit);
        command.Parameters.Add(Timestamp("before", query.Before));

        var found = new List<MiHoldingState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            found.Add(new MiHoldingState(
                reader.GetString(0),
                reader.GetString(1),
                ParseHoldingStatus(reader.GetString(2)),
                reader.GetBoolean(3),
                reader.GetInt32(4),
                Value<DateTimeOffset>(reader, 5),
                Value<DateOnly>(reader, 6),
                Value<DateTimeOffset>(reader, 7),
                Value<DateTimeOffset>(reader, 8),
                Value<Guid>(reader, 9),
                Text(reader, 10)));
        }

        return found;
    }

    private static async Task<string> FingerprintAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string groupId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(FingerprintSql, connection, transaction);
        command.Parameters.AddWithValue("group", groupId);
        return await command.ExecuteScalarAsync(cancellationToken) as string ?? string.Empty;
    }

    private static async Task ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string groupId,
        string sql,
        Action<NpgsqlDataReader> read,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("group", groupId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            read(reader);
        }
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
        }

        return connection;
    }

    private static NpgsqlParameter Timestamp(string name, DateTimeOffset? value) =>
        new(name, NpgsqlDbType.TimestampTz) { Value = value is { } at ? at.ToUniversalTime() : DBNull.Value };

    private static HoldingStatus ParseHoldingStatus(string value) =>
        HoldingStatuses.TryParse(value, out var status)
            ? status
            : throw new InvalidOperationException($"'{value}' is not a known holding status.");

    private static string? Text(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static T? Value<T>(NpgsqlDataReader reader, int ordinal) where T : struct =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);

    private static T? Parse<T>(NpgsqlDataReader reader, int ordinal, Func<string, T> parse) where T : struct =>
        reader.IsDBNull(ordinal) ? null : parse(reader.GetString(ordinal));

    private static Task TextAsync(NpgsqlBinaryImporter writer, string? value, CancellationToken cancellationToken) =>
        value is null ? writer.WriteNullAsync(cancellationToken) : writer.WriteAsync(value, NpgsqlDbType.Text, cancellationToken);

    private static Task ValueAsync<T>(NpgsqlBinaryImporter writer, T? value, NpgsqlDbType type, CancellationToken cancellationToken)
        where T : struct =>
        value is { } present ? writer.WriteAsync(present, type, cancellationToken) : writer.WriteNullAsync(cancellationToken);
}
