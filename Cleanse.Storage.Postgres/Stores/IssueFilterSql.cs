using System.Text;
using System.Text.Json;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Storage.Postgres.Schema;
using Npgsql;
using NpgsqlTypes;

namespace Cleanse.Storage.Postgres.Stores;

/// <summary>
/// Turns the declarative <see cref="IssueFilter"/> into a WHERE clause. Every caller-supplied
/// value travels as a parameter; only column and keyword text is ever concatenated, so a search
/// term cannot reach the parser.
/// </summary>
internal static class IssueFilterSql
{
    private static readonly string Whitespace = string.Concat(
        Enumerable.Range(char.MinValue, char.MaxValue + 1).Select(value => (char)value).Where(char.IsWhiteSpace));

    internal sealed record Clause(string Sql, IReadOnlyList<NpgsqlParameter> Parameters)
    {
        public void AddParameters(NpgsqlCommand command)
        {
            foreach (var parameter in Parameters)
            {
                command.Parameters.Add(parameter.Clone());
            }
        }
    }

    public static Clause Build(IssueFilter filter)
    {
        var predicates = new List<string>();
        var parameters = new List<NpgsqlParameter>();

        void Add(string sql, string name, object value, NpgsqlDbType? type = null)
        {
            predicates.Add(sql);
            parameters.Add(type is { } t
                ? new NpgsqlParameter(name, t) { Value = value }
                : new NpgsqlParameter(name, value));
        }

        if (filter.GroupId is { } group)
        {
            Add($"{Db.Issue.GroupId} = @f_group", "f_group", group);
        }

        if (filter.RuleId is { } rule)
        {
            Add($"{Db.Issue.RuleId} = @f_rule", "f_rule", rule);
        }

        if (filter.RunId is { } run)
        {
            Add($"{Db.Issue.RunId} = @f_run", "f_run", run);
        }

        if (filter.Status is { } status)
        {
            Add($"{Db.Issue.Status} = @f_status", "f_status", status.ToWire());
        }

        if (filter.WorkflowStatuses is { Count: > 0 } workflow)
        {
            Add($"{Db.Issue.WorkflowStatus} = ANY(@f_workflow)",
                "f_workflow",
                workflow.Select(w => w.ToWire()).ToArray(),
                NpgsqlDbType.Array | NpgsqlDbType.Text);
        }

        // Assignment belongs to the holding: an issue matches through the holding its CPH names.
        if (filter.AssignedTo is { } assignee)
        {
            Add(HoldingExists($"LOWER(h.{Db.Holding.AssignedTo}) = LOWER(@f_assignee)"), "f_assignee", assignee);
        }

        if (filter.AssignedUserId is { } userId)
        {
            Add(HoldingExists($"h.{Db.Holding.AssignedUserId} = @f_user"), "f_user", userId, NpgsqlDbType.Uuid);
        }

        if (filter.Unassigned is { } unassigned)
        {
            parameters.Add(new NpgsqlParameter("f_whitespace", Whitespace));
            var assigned = HoldingExists(
                $"(h.{Db.Holding.AssignedUserId} IS NOT NULL OR BTRIM(h.{Db.Holding.AssignedTo}, @f_whitespace) <> '')");
            predicates.Add(unassigned ? $"NOT {assigned}" : assigned);
        }

        if (filter.CreatedAfterUtc is { } createdAfter)
        {
            Add($"{Db.Issue.CreatedAtUtc} >= @f_created_after",
                "f_created_after", createdAfter, NpgsqlDbType.TimestampTz);
        }

        if (filter.CreatedBeforeUtc is { } createdBefore)
        {
            Add($"{Db.Issue.CreatedAtUtc} <= @f_created_before",
                "f_created_before", createdBefore, NpgsqlDbType.TimestampTz);
        }

        if (filter.UpdatedAfterUtc is { } updatedAfter)
        {
            Add($"{Db.Issue.UpdatedAtUtc} >= @f_updated_after",
                "f_updated_after", updatedAfter, NpgsqlDbType.TimestampTz);
        }

        if (filter.UpdatedBeforeUtc is { } updatedBefore)
        {
            Add($"{Db.Issue.UpdatedAtUtc} <= @f_updated_before",
                "f_updated_before", updatedBefore, NpgsqlDbType.TimestampTz);
        }

        if (filter.MinimumAge is { } minimumAge)
        {
            Add($"{Db.Issue.CreatedAtUtc} <= @f_raised_by",
                "f_raised_by", minimumAge.RaisedByUtc, NpgsqlDbType.TimestampTz);

            // Comparing intervals counts a day as 24 hours, as the other providers do, whatever the session time zone.
            Add($"({Db.Issue.DeactivatedAtUtc} IS NULL OR {Db.Issue.DeactivatedAtUtc} - {Db.Issue.CreatedAtUtc} >= @f_minimum_age)",
                "f_minimum_age", minimumAge.Age, NpgsqlDbType.Interval);
        }

        if (filter.ContextMatches is { Count: > 0 } context)
        {
            // Containment, so the jsonb_path_ops GIN index can serve it.
            Add($"{Db.Issue.Context} @> @f_context",
                "f_context",
                JsonSerializer.Serialize(context),
                NpgsqlDbType.Jsonb);
        }

        if (filter.SearchTerm is { } term && !string.IsNullOrWhiteSpace(term))
        {
            predicates.Add($"""
                (
                    {Db.Issue.Id} ILIKE @f_term
                    OR {Db.Issue.Notes} ILIKE @f_term
                    OR EXISTS (
                        SELECT 1 FROM jsonb_each_text({Db.Issue.Context}) AS ctx(k, v)
                        WHERE ctx.v ILIKE @f_term
                    )
                )
                """);
            parameters.Add(new NpgsqlParameter("f_term", $"%{EscapeLike(term.Trim())}%"));
        }

        var sql = predicates.Count == 0
            ? string.Empty
            : "WHERE " + string.Join("\n  AND ", predicates);

        return new Clause(sql, parameters);
    }

    /// <summary>Orders the grouped query, which exposes issue_count and latest_updated_at_utc.</summary>
    public static string GroupOrderBy(IssueFilter filter)
    {
        var direction = filter.GroupSortDirection == SortDirection.Descending ? "DESC" : "ASC";
        var leading = filter.GroupSortBy == IssueGroupSortField.IssueCount
            ? $"issue_count {direction}, latest_updated_at_utc DESC"
            : $"latest_updated_at_utc {direction}";

        return $"{leading}, group_key COLLATE \"C\" ASC";
    }

    public static string OrderBy(IssueFilter filter)
    {
        var direction = filter.SortDirection == SortDirection.Descending ? "DESC" : "ASC";
        var column = filter.SortBy switch
        {
            IssueSortField.CreatedAtUtc => Db.Issue.CreatedAtUtc,
            IssueSortField.Id => Db.Issue.Id,
            IssueSortField.RuleId => Db.Issue.RuleId,
            IssueSortField.WorkflowStatus => Db.Issue.WorkflowStatus,
            _ => Db.Issue.UpdatedAtUtc
        };

        // Id is always the final key so paging cannot repeat or skip rows on equal sort values.
        return column == Db.Issue.Id
            ? $"{Db.Issue.Id} {direction}"
            : $"{column} {direction}, {Db.Issue.Id} ASC";
    }

    /// <summary>A predicate on the issue's holding row, found through its primary key.</summary>
    private static string HoldingExists(string condition) => $"""
        EXISTS (
            SELECT 1 FROM {Db.Holdings} h
            WHERE h.{Db.Holding.GroupId} = {Db.Issues}.{Db.Issue.GroupId}
              AND h.{Db.Holding.Cph} = COALESCE({Db.Issues}.{Db.Issue.Context} ->> 'cph', '')
              AND {condition}
        )
        """;

    /// <summary>LIKE wildcards inside a user's search term must match literally.</summary>
    private static string EscapeLike(string term)
    {
        var builder = new StringBuilder(term.Length);
        foreach (var c in term)
        {
            if (c is '%' or '_' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
