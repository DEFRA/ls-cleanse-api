using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cleanse.Storage.Postgres.Schema.Migrations
{
    /// <inheritdoc />
    public partial class MiDashboard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mi_daily_changes",
                columns: table => new
                {
                    group_id = table.Column<string>(type: "text", nullable: false),
                    unit = table.Column<string>(type: "text", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    rule_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    delta = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mi_daily_changes", x => new { x.group_id, x.unit, x.day, x.rule_id, x.status, x.state });
                });

            migrationBuilder.CreateTable(
                name: "mi_events",
                columns: table => new
                {
                    group_id = table.Column<string>(type: "text", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    unit = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    issue_id = table.Column<string>(type: "text", nullable: true),
                    rule_id = table.Column<string>(type: "text", nullable: true),
                    cph = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_name = table.Column<string>(type: "text", nullable: true),
                    from_status = table.Column<string>(type: "text", nullable: true),
                    to_status = table.Column<string>(type: "text", nullable: true),
                    from_workflow = table.Column<string>(type: "text", nullable: true),
                    to_workflow = table.Column<string>(type: "text", nullable: true),
                    reason_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason_name = table.Column<string>(type: "text", nullable: true),
                    open_change = table.Column<string>(type: "text", nullable: true),
                    open_delta = table.Column<int>(type: "integer", nullable: false),
                    elapsed_days = table.Column<int>(type: "integer", nullable: true),
                    inactive_now = table.Column<bool>(type: "boolean", nullable: true),
                    actions_since_closed = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mi_events", x => new { x.group_id, x.sequence });
                });

            migrationBuilder.CreateTable(
                name: "mi_holding_states",
                columns: table => new
                {
                    group_id = table.Column<string>(type: "text", nullable: false),
                    cph = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    has_active_issue = table.Column<bool>(type: "boolean", nullable: false),
                    open_issues = table.Column<int>(type: "integer", nullable: false),
                    oldest_open_raised_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    oldest_open_raised_day = table.Column<DateOnly>(type: "date", nullable: true),
                    in_progress_since_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_worked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    assigned_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assignee_name = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mi_holding_states", x => new { x.group_id, x.cph });
                });

            migrationBuilder.CreateTable(
                name: "mi_issue_states",
                columns: table => new
                {
                    group_id = table.Column<string>(type: "text", nullable: false),
                    issue_id = table.Column<string>(type: "text", nullable: false),
                    rule_id = table.Column<string>(type: "text", nullable: false),
                    cph = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    workflow_status = table.Column<string>(type: "text", nullable: false),
                    reason_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason_name = table.Column<string>(type: "text", nullable: true),
                    first_raised_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    first_raised_day = table.Column<DateOnly>(type: "date", nullable: false),
                    last_changed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mi_issue_states", x => new { x.group_id, x.issue_id });
                });

            migrationBuilder.CreateTable(
                name: "mi_refresh",
                columns: table => new
                {
                    group_id = table.Column<string>(type: "text", nullable: false),
                    built_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    checked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fingerprint = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    duration_milliseconds = table.Column<int>(type: "integer", nullable: false),
                    history_from_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    issues = table.Column<int>(type: "integer", nullable: false),
                    events = table.Column<int>(type: "integer", nullable: false),
                    mismatches = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mi_refresh", x => x.group_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_mi",
                table: "audit_entries",
                column: "sequence",
                filter: "issue_id IS NOT NULL AND kind IN ('IssueReactivated', 'IssueWorkflowStatusChanged', 'IssueSuppressed', 'IssueSuppressionExpired', 'IssueExternalStateApplied')");

            migrationBuilder.CreateIndex(
                name: "ix_mi_events_day",
                table: "mi_events",
                columns: new[] { "group_id", "day" });

            migrationBuilder.CreateIndex(
                name: "ix_mi_events_person",
                table: "mi_events",
                columns: new[] { "group_id", "user_id", "day" },
                filter: "source = 'cleanse'");

            migrationBuilder.CreateIndex(
                name: "ix_mi_holding_states_open",
                table: "mi_holding_states",
                columns: new[] { "group_id", "oldest_open_raised_at_utc" },
                filter: "status IN ('todo', 'in-progress')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mi_daily_changes");

            migrationBuilder.DropTable(
                name: "mi_events");

            migrationBuilder.DropTable(
                name: "mi_holding_states");

            migrationBuilder.DropTable(
                name: "mi_issue_states");

            migrationBuilder.DropTable(
                name: "mi_refresh");

            migrationBuilder.DropIndex(
                name: "ix_audit_entries_mi",
                table: "audit_entries");
        }
    }
}
