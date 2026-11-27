using System.Globalization;
using System.Text;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Mi;
using Cleanse.Storage.Sqlite.Schema;
using Microsoft.Data.Sqlite;

namespace Cleanse.Storage.Sqlite.Stores;

/// <summary>
/// The dashboard's summary in SQLite. One connection serialises writers here, so a replace never
/// has to wait for another; the history is read in one transaction so it is one snapshot.
/// </summary>
internal sealed class SqliteMiStore(CleanseSqliteDatabase database) : IMiStore
{
    private const string DayFormat = "yyyy-MM-dd";

    private static readonly string[] SummaryTables =
        ["mi_events", "mi_daily_changes", "mi_issue_states", "mi_holding_states", "mi_refresh"];

    // Notes, actions and audit entries are only ever added, so a count and a high-water mark see
    // every change, and every change to an issue or holding moves its version. SQLite has one
    // writer, so audit numbers commit in order and the mark alone is enough there; it spans every
    // group, which can only cause a refresh that finds nothing new.
    private const string FingerprintSql = """
        SELECT (SELECT COUNT(*) || ':' || COALESCE(SUM(version), 0) FROM issues WHERE group_id = $group)
            || '|' || (SELECT COALESCE(MAX(sequence), 0) FROM audit_entries)
            || '|' || (SELECT COUNT(*) || ':' || COALESCE(MAX(sequence), 0) FROM holding_notes WHERE group_id = $group)
            || '|' || (SELECT COUNT(*) || ':' || COALESCE(MAX(sequence), 0) FROM holding_actions WHERE group_id = $group)
            || '|' || (SELECT COUNT(*) || ':' || COALESCE(SUM(version), 0) FROM holdings WHERE group_id = $group)
            || '|' || (SELECT COALESCE(MIN(started_at_utc), '') FROM rule_runs WHERE group_id = $group)
        """;

    private const string HoldingColumns =
        "group_id, cph, status, has_active_issue, open_issues, oldest_open_raised_at_utc, oldest_open_raised_day, " +
        "in_progress_since_utc, last_worked_at_utc, assigned_user_id, assignee_name";

    private delegate MiEventCount ReadDimension(MiEventCount count, SqliteDataReader reader, int ordinal);

    /// <summary>What each dimension groups by. Only these column names ever reach the SQL.</summary>
    private static readonly (MiEventDimensions Flag, string Column, ReadDimension Read)[] Dimensions =
    [
        (MiEventDimensions.Day, "day", (c, r, i) => c with { Day = Parse(r, i, ParseDay) }),
        (MiEventDimensions.Unit, "unit", (c, r, i) => c with { Unit = Parse(r, i, MiNames.ParseUnit) }),
        (MiEventDimensions.Kind, "kind", (c, r, i) => c with { Kind = Parse(r, i, MiNames.ParseKind) }),
        (MiEventDimensions.Source, "source", (c, r, i) => c with { Source = Parse(r, i, MiNames.ParseSource) }),
        (MiEventDimensions.User, "user_id", (c, r, i) => c with { UserId = Parse(r, i, Guid.Parse) }),
        (MiEventDimensions.Rule, "rule_id", (c, r, i) => c with { RuleId = Text(r, i) }),
        (MiEventDimensions.Cph, "cph", (c, r, i) => c with { Cph = Text(r, i) }),
        (MiEventDimensions.FromStatus, "from_status", (c, r, i) => c with { FromStatus = Text(r, i) }),
        (MiEventDimensions.ToStatus, "to_status", (c, r, i) => c with { ToStatus = Text(r, i) }),
        (MiEventDimensions.FromWorkflow, "from_workflow", (c, r, i) => c with { FromWorkflow = Text(r, i) }),
        (MiEventDimensions.ToWorkflow, "to_workflow", (c, r, i) => c with { ToWorkflow = Text(r, i) }),
        (MiEventDimensions.Reason, "reason_id", (c, r, i) => c with { ReasonId = Parse(r, i, Guid.Parse) }),
        (MiEventDimensions.Reason, "reason_name", (c, r, i) => c with { ReasonName = Text(r, i) }),
        (MiEventDimensions.OpenChange, "open_change", (c, r, i) => c with { OpenChange = Parse(r, i, MiNames.ParseOpenChange) }),
        (MiEventDimensions.OpenDelta, "open_delta", (c, r, i) => c with { OpenDelta = Number(r, i) }),
        (MiEventDimensions.ElapsedDays, "elapsed_days", (c, r, i) => c with { ElapsedDays = Number(r, i) }),
        (MiEventDimensions.InactiveNow, "inactive_now", (c, r, i) => c with { InactiveNow = r.IsDBNull(i) ? null : r.GetInt64(i) != 0 }),
        (MiEventDimensions.ActionsSinceClosed, "actions_since_closed", (c, r, i) => c with { ActionsSinceClosed = Number(r, i) })
    ];

    public async Task<string> GetFingerprintAsync(string groupId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        await using var connection = await database.OpenAsync(cancellationToken);
        return await FingerprintAsync(connection, null, groupId, cancellationToken);
    }

    public async Task<MiHistory> ReadHistoryAsync(string groupId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);

        var fingerprint = await FingerprintAsync(connection, transaction, groupId, cancellationToken);

        var issues = new List<MiHistoryIssue>();
        await ReadAsync(connection, transaction, groupId, """
            SELECT id, rule_id, json_extract(context, '$.cph'), status, workflow_status, outcome_reason_id,
                   outcome_reason, created_at_utc, deactivated_at_utc, updated_at_utc
            FROM issues
            WHERE group_id = $group
            """, reader =>
        {
            var cph = Text(reader, 2);
            issues.Add(new MiHistoryIssue(
                reader.GetString(0),
                reader.GetString(1),
                string.IsNullOrWhiteSpace(cph) ? null : cph,
                SqliteMappings.ParseIssueStatus(reader.GetString(3)),
                SqliteMappings.ParseWorkflowStatus(reader.GetString(4)),
                Parse(reader, 5, Guid.Parse),
                Text(reader, 6),
                SqliteMappings.ToTimestamp(reader.GetString(7)),
                Parse(reader, 8, SqliteMappings.ToTimestamp),
                SqliteMappings.ToTimestamp(reader.GetString(9))));
        }, cancellationToken);

        var kinds = string.Join(", ", MiAuditFact.Kinds.Select(kind => $"'{kind}'"));
        var facts = new List<MiAuditFact>();
        await ReadAsync(connection, transaction, groupId, $"""
            SELECT a.sequence, a.id, a.issue_id, a.kind, a.actor_id, a.actor_display_name, a.actor_kind,
                   a.at_utc, a.changes
            FROM audit_entries a
            JOIN issues i ON i.id = a.issue_id
            WHERE i.group_id = $group AND a.issue_id IS NOT NULL AND a.kind IN ({kinds})
            """, reader =>
        {
            var entry = AuditEntry.Rehydrate(
                Guid.Parse(reader.GetString(1)),
                IssueId.From(reader.GetString(2)),
                null,
                Enum.Parse<AuditEventKind>(reader.GetString(3)),
                SqliteMappings.RebuildActor(reader.GetString(4), reader.GetString(5), reader.GetString(6)),
                SqliteMappings.ToTimestamp(reader.GetString(7)),
                SqliteMappings.DeserialiseChanges(reader.GetString(8)));
            if (MiAuditFact.From(entry, reader.GetInt64(0)) is { } fact)
            {
                facts.Add(fact);
            }
        }, cancellationToken);

        var holdings = new List<MiHistoryHolding>();
        await ReadAsync(connection, transaction, groupId, """
            SELECT cph, assigned_user_id, assigned_to, assigned_user_full_name
            FROM holdings
            WHERE group_id = $group
            """, reader => holdings.Add(new MiHistoryHolding(
                reader.GetString(0), Parse(reader, 1, Guid.Parse), Text(reader, 2), Text(reader, 3))),
            cancellationToken);

        var activity = new List<MiHistoryActivity>();
        await ReadAsync(connection, transaction, groupId, """
            SELECT sequence, cph, created_at_utc, created_by_id, created_by_display_name, created_by_kind,
                   action_type_id, action_type_name
            FROM holding_actions
            WHERE group_id = $group
            """, reader => activity.Add(new MiHistoryActivity(
                MiUnit.Action,
                reader.GetString(1),
                SqliteMappings.ToTimestamp(reader.GetString(2)),
                reader.GetInt64(0),
                SqliteMappings.RebuildActor(reader.GetString(3), reader.GetString(4), reader.GetString(5)),
                Guid.Parse(reader.GetString(6)),
                reader.GetString(7))),
            cancellationToken);

        await ReadAsync(connection, transaction, groupId, """
            SELECT sequence, cph, created_at_utc, created_by_id, created_by_display_name, created_by_kind
            FROM holding_notes
            WHERE group_id = $group
            """, reader => activity.Add(new MiHistoryActivity(
                MiUnit.Note,
                reader.GetString(1),
                SqliteMappings.ToTimestamp(reader.GetString(2)),
                reader.GetInt64(0),
                SqliteMappings.RebuildActor(reader.GetString(3), reader.GetString(4), reader.GetString(5)))),
            cancellationToken);

        DateTimeOffset? firstRun = null;
        await ReadAsync(connection, transaction, groupId,
            "SELECT MIN(started_at_utc) FROM rule_runs WHERE group_id = $group",
            reader => firstRun = Parse(reader, 0, SqliteMappings.ToTimestamp),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new MiHistory(groupId, fingerprint, issues, facts, holdings, activity, firstRun);
    }

    public async Task<bool> ReplaceAsync(MiSummary summary, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var groupId = summary.Refresh.GroupId;

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        foreach (var table in SummaryTables)
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {table} WHERE group_id = $group";
            delete.Bind("$group", groupId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = new Insert(connection, transaction, "mi_events",
            "group_id", "sequence", "at_utc", "day", "unit", "kind", "source", "issue_id", "rule_id", "cph",
            "user_id", "user_name", "from_status", "to_status", "from_workflow", "to_workflow", "reason_id",
            "reason_name", "open_change", "open_delta", "elapsed_days", "inactive_now", "actions_since_closed"))
        {
            foreach (var e in summary.Events)
            {
                await insert.RunAsync(
                [
                    e.GroupId, e.Sequence, e.AtUtc.ToText(), ToText(e.Day), e.Unit.ToWire(), e.Kind.ToWire(),
                    e.Source.ToWire(), e.IssueId, e.RuleId, e.Cph, e.UserId?.ToString(), e.UserName, e.FromStatus,
                    e.ToStatus, e.FromWorkflow, e.ToWorkflow, e.ReasonId?.ToString(), e.ReasonName,
                    e.OpenChange?.ToWire(), e.OpenDelta, e.ElapsedDays, e.InactiveNow is { } now ? now ? 1 : 0 : null,
                    e.ActionsSinceClosed
                ], cancellationToken);
            }
        }

        await using (var insert = new Insert(connection, transaction, "mi_daily_changes",
            "group_id", "unit", "day", "rule_id", "status", "state", "delta"))
        {
            foreach (var change in summary.DailyChanges)
            {
                await insert.RunAsync(
                [
                    change.GroupId, change.Unit.ToWire(), ToText(change.Day), change.RuleId, change.Status,
                    change.State, change.Delta
                ], cancellationToken);
            }
        }

        await using (var insert = new Insert(connection, transaction, "mi_issue_states",
            "group_id", "issue_id", "rule_id", "cph", "status", "workflow_status", "reason_id", "reason_name",
            "first_raised_at_utc", "first_raised_day", "last_changed_at_utc"))
        {
            foreach (var issue in summary.Issues)
            {
                await insert.RunAsync(
                [
                    issue.GroupId, issue.IssueId, issue.RuleId, issue.Cph, issue.Status.ToWire(),
                    issue.WorkflowStatus.ToWire(), issue.ReasonId?.ToString(), issue.ReasonName,
                    issue.FirstRaisedAtUtc.ToText(), ToText(issue.FirstRaisedDay), issue.LastChangedAtUtc.ToText()
                ], cancellationToken);
            }
        }

        await using (var insert = new Insert(connection, transaction, "mi_holding_states", HoldingColumns.Split(", ")))
        {
            foreach (var holding in summary.Holdings)
            {
                await insert.RunAsync(
                [
                    holding.GroupId, holding.Cph, holding.Status.ToWire(), holding.HasActiveIssue ? 1 : 0,
                    holding.OpenIssues, holding.OldestOpenRaisedAtUtc?.ToText(),
                    holding.OldestOpenRaisedDay is { } day ? ToText(day) : null, holding.InProgressSinceUtc?.ToText(),
                    holding.LastWorkedAtUtc?.ToText(), holding.AssignedUserId?.ToString(), holding.AssigneeName
                ], cancellationToken);
            }
        }

        var refresh = summary.Refresh;
        await using (var insert = new Insert(connection, transaction, "mi_refresh",
            "group_id", "built_at_utc", "checked_at_utc", "fingerprint", "version", "duration_milliseconds",
            "history_from_utc", "issues", "events", "mismatches"))
        {
            await insert.RunAsync(
            [
                refresh.GroupId, refresh.BuiltAtUtc.ToText(), refresh.CheckedAtUtc.ToText(), refresh.Fingerprint,
                refresh.Version, refresh.DurationMilliseconds, refresh.HistoryFromUtc?.ToText(), refresh.Issues,
                refresh.Events, refresh.Mismatches
            ], cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<MiRefreshRecord?> GetRefreshAsync(string groupId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        MiRefreshRecord? found = null;
        await ReadAsync(connection, null, groupId, """
            SELECT group_id, built_at_utc, checked_at_utc, fingerprint, version, duration_milliseconds,
                   history_from_utc, issues, events, mismatches
            FROM mi_refresh
            WHERE group_id = $group
            """, reader => found = new MiRefreshRecord(
                reader.GetString(0),
                SqliteMappings.ToTimestamp(reader.GetString(1)),
                SqliteMappings.ToTimestamp(reader.GetString(2)),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                Parse(reader, 6, SqliteMappings.ToTimestamp),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetInt32(9)),
            cancellationToken);
        return found;
    }

    public async Task MarkCheckedAsync(string groupId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE mi_refresh SET checked_at_utc = $at WHERE group_id = $group";
        command.Bind("$group", groupId);
        command.Bind("$at", at.ToText());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MiEventCount>> CountEventsAsync(
        MiEventFilter filter,
        MiEventDimensions by,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var selected = Dimensions.Where(dimension => by.HasFlag(dimension.Flag)).ToList();
        var columns = string.Join(", ", selected.Select(dimension => dimension.Column));
        var sql = new StringBuilder("SELECT COUNT(*), MAX(at_utc)");
        if (selected.Count > 0)
        {
            sql.Append(", ").Append(columns);
        }

        sql.Append(" FROM mi_events WHERE group_id = $group AND day >= $from AND day <= $to");
        command.Bind("$group", filter.GroupId);
        command.Bind("$from", ToText(filter.FromDay));
        command.Bind("$to", ToText(filter.ToDay));

        void In<T>(string column, IReadOnlyCollection<T>? values, Func<T, string> toWire)
        {
            if (values is null)
            {
                return;
            }

            var names = values.Select((value, index) =>
            {
                var name = $"${column}{index}";
                command.Bind(name, toWire(value));
                return name;
            }).ToList();
            sql.Append(names.Count == 0
                ? " AND 0"
                : $" AND {column} IN ({string.Join(", ", names)})");
        }

        In("unit", filter.Units, MiNames.ToWire);
        In("kind", filter.Kinds, MiNames.ToWire);
        In("source", filter.Sources, MiNames.ToWire);
        if (filter.RuleId is not null)
        {
            sql.Append(" AND rule_id = $rule");
            command.Bind("$rule", filter.RuleId);
        }

        if (filter.UserId is { } user)
        {
            sql.Append(" AND user_id = $user");
            command.Bind("$user", user.ToString());
        }

        sql.Append(selected.Count > 0 ? $" GROUP BY {columns}" : " HAVING COUNT(*) > 0");
        command.CommandText = sql.ToString();

        var counts = new List<MiEventCount>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var count = new MiEventCount(reader.GetInt32(0), SqliteMappings.ToTimestamp(reader.GetString(1)));
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
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT day, rule_id, status, state, delta
            FROM mi_daily_changes
            WHERE group_id = $group AND unit = $unit AND day <= $to{(ruleId is null ? string.Empty : " AND rule_id = $rule")}
            """;
        command.Bind("$group", groupId);
        command.Bind("$unit", unit.ToWire());
        command.Bind("$to", ToText(toDay));
        if (ruleId is not null)
        {
            command.Bind("$rule", ruleId);
        }

        var changes = new List<MiDailyChange>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            changes.Add(new MiDailyChange(
                groupId, ParseDay(reader.GetString(0)), unit, reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt32(4)));
        }

        return changes;
    }

    public async Task<IReadOnlyList<MiIssueStateCount>> CountIssueStatesAsync(
        string groupId,
        string? ruleId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT rule_id, status, workflow_status, first_raised_day, COUNT(*)
            FROM mi_issue_states
            WHERE group_id = $group{(ruleId is null ? string.Empty : " AND rule_id = $rule")}
            GROUP BY rule_id, status, workflow_status, first_raised_day
            """;
        command.Bind("$group", groupId);
        if (ruleId is not null)
        {
            command.Bind("$rule", ruleId);
        }

        var counts = new List<MiIssueStateCount>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            counts.Add(new MiIssueStateCount(
                reader.GetString(0),
                SqliteMappings.ParseIssueStatus(reader.GetString(1)),
                SqliteMappings.ParseWorkflowStatus(reader.GetString(2)),
                ParseDay(reader.GetString(3)),
                reader.GetInt32(4)));
        }

        return counts;
    }

    public async Task<IReadOnlyList<MiHoldingStateCount>> CountHoldingStatesAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var counts = new List<MiHoldingStateCount>();
        await ReadAsync(connection, null, groupId, """
            SELECT status, has_active_issue, assigned_user_id, oldest_open_raised_day, COUNT(*)
            FROM mi_holding_states
            WHERE group_id = $group
            GROUP BY status, has_active_issue, assigned_user_id, oldest_open_raised_day
            """, reader => counts.Add(new MiHoldingStateCount(
                ParseHoldingStatus(reader.GetString(0)),
                reader.GetInt64(1) != 0,
                Parse(reader, 2, Guid.Parse),
                Parse(reader, 3, ParseDay),
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

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = query.List == MiHoldingList.Stalled
            ? $"""
              SELECT {HoldingColumns} FROM mi_holding_states
              WHERE group_id = $group AND status = $in_progress
                AND in_progress_since_utc IS NOT NULL AND in_progress_since_utc <= $before
                AND (last_worked_at_utc IS NULL OR last_worked_at_utc <= $before)
              ORDER BY COALESCE(last_worked_at_utc, in_progress_since_utc), cph
              LIMIT $limit
              """
            : $"""
              SELECT {HoldingColumns} FROM mi_holding_states
              WHERE group_id = $group AND status IN ($todo, $in_progress) AND oldest_open_raised_at_utc IS NOT NULL
              ORDER BY oldest_open_raised_at_utc, cph
              LIMIT $limit
              """;
        command.Bind("$group", query.GroupId);
        command.Bind("$in_progress", HoldingStatus.InProgress.ToWire());
        command.Bind("$limit", query.Limit);
        if (query.List == MiHoldingList.Stalled)
        {
            command.Bind("$before", query.Before!.Value.ToText());
        }
        else
        {
            command.Bind("$todo", HoldingStatus.Todo.ToWire());
        }

        var found = new List<MiHoldingState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            found.Add(new MiHoldingState(
                reader.GetString(0),
                reader.GetString(1),
                ParseHoldingStatus(reader.GetString(2)),
                reader.GetInt64(3) != 0,
                reader.GetInt32(4),
                Parse(reader, 5, SqliteMappings.ToTimestamp),
                Parse(reader, 6, ParseDay),
                Parse(reader, 7, SqliteMappings.ToTimestamp),
                Parse(reader, 8, SqliteMappings.ToTimestamp),
                Parse(reader, 9, Guid.Parse),
                Text(reader, 10)));
        }

        return found;
    }

    private static async Task<string> FingerprintAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string groupId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = FingerprintSql;
        command.Bind("$group", groupId);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string groupId,
        string sql,
        Action<SqliteDataReader> read,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Bind("$group", groupId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            read(reader);
        }
    }

    private static string ToText(DateOnly day) => day.ToString(DayFormat, CultureInfo.InvariantCulture);

    private static DateOnly ParseDay(string value) => DateOnly.ParseExact(value, DayFormat, CultureInfo.InvariantCulture);

    private static HoldingStatus ParseHoldingStatus(string value) =>
        HoldingStatuses.TryParse(value, out var status)
            ? status
            : throw new InvalidOperationException($"'{value}' is not a known holding status.");

    private static string? Text(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? Number(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static T? Parse<T>(SqliteDataReader reader, int ordinal, Func<string, T> parse) where T : struct =>
        reader.IsDBNull(ordinal) ? null : parse(reader.GetString(ordinal));

    /// <summary>One prepared insert, run once per row.</summary>
    private sealed class Insert : IAsyncDisposable
    {
        private readonly SqliteCommand _command;
        private readonly SqliteParameter[] _parameters;

        public Insert(SqliteConnection connection, SqliteTransaction transaction, string table, params string[] columns)
        {
            _command = connection.CreateCommand();
            _command.Transaction = transaction;
            _command.CommandText =
                $"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select(c => "$" + c))})";
            _parameters = [.. columns.Select(column => _command.Parameters.Add(new SqliteParameter { ParameterName = "$" + column }))];
        }

        public async Task RunAsync(object?[] values, CancellationToken cancellationToken)
        {
            for (var i = 0; i < _parameters.Length; i++)
            {
                _parameters[i].Value = values[i] ?? DBNull.Value;
            }

            await _command.ExecuteNonQueryAsync(cancellationToken);
        }

        public ValueTask DisposeAsync() => _command.DisposeAsync();
    }
}
