using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cleanse.Storage.Postgres.Schema.Migrations
{
    /// <inheritdoc />
    public partial class HoldingWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_issues_workflow_status",
                table: "issues");

            // Ignored merges into Can't resolve, which is stored as 'nonactionable'. The converted
            // issues have no reason: one is only required for outcomes given from now on.
            migrationBuilder.Sql(
                "UPDATE issues SET workflow_status = 'nonactionable' WHERE workflow_status = 'ignored';");

            migrationBuilder.AddColumn<string>(
                name: "outcome_reason",
                table: "issues",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "outcome_reason_id",
                table: "issues",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "subject",
                table: "audit_entries",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "coded_values",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    list = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_coded_values", x => x.id);
                    table.CheckConstraint("ck_coded_values_list", "list IN ('action-types', 'resolved-reasons', 'cant-resolve-reasons')");
                });

            migrationBuilder.CreateTable(
                name: "holding_notes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    group_id = table.Column<string>(type: "text", nullable: false),
                    cph = table.Column<string>(type: "text", nullable: false),
                    text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    context = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_id = table.Column<string>(type: "text", nullable: false),
                    created_by_display_name = table.Column<string>(type: "text", nullable: false),
                    created_by_kind = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holding_notes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "holdings",
                columns: table => new
                {
                    group_id = table.Column<string>(type: "text", nullable: false),
                    cph = table.Column<string>(type: "text", nullable: false),
                    assigned_to = table.Column<string>(type: "text", nullable: true),
                    assigned_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_user_full_name = table.Column<string>(type: "text", nullable: true),
                    assignment_detached_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holdings", x => new { x.group_id, x.cph });
                    table.ForeignKey(
                        name: "fk_holdings_assigned_user",
                        column: x => x.assigned_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "holding_actions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<string>(type: "text", nullable: false),
                    cph = table.Column<string>(type: "text", nullable: false),
                    action_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action_type_name = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_id = table.Column<string>(type: "text", nullable: false),
                    created_by_display_name = table.Column<string>(type: "text", nullable: false),
                    created_by_kind = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holding_actions", x => x.id);
                    table.ForeignKey(
                        name: "fk_holding_actions_action_type",
                        column: x => x.action_type_id,
                        principalTable: "coded_values",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_issues_outcome_reason_id",
                table: "issues",
                column: "outcome_reason_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_issues_workflow_status",
                table: "issues",
                sql: "workflow_status IN ('todo', 'in-progress', 'suppressed', 'resolved', 'nonactionable')");

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_subject",
                table: "audit_entries",
                columns: new[] { "subject", "at_utc", "sequence" },
                descending: new[] { false, true, true },
                filter: "subject IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_coded_values_list_name",
                table: "coded_values",
                columns: new[] { "list", "name_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_holding_actions_action_type_id",
                table: "holding_actions",
                column: "action_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_holding_actions_created",
                table: "holding_actions",
                column: "created_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_holding_actions_holding",
                table: "holding_actions",
                columns: new[] { "group_id", "cph", "created_at_utc", "sequence" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_holding_notes_holding",
                table: "holding_notes",
                columns: new[] { "group_id", "cph", "created_at_utc", "sequence" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_holdings_assigned_user",
                table: "holdings",
                column: "assigned_user_id",
                filter: "assigned_user_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_issues_outcome_reason",
                table: "issues",
                column: "outcome_reason_id",
                principalTable: "coded_values",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Assignment moves from the issue to the holding. Where a holding's issues disagree,
            // one assignee wins: one made in Cleanse beats one from SPOL, then one on an open issue
            // beats one on a closed issue, then the most recently assigned. The issue columns are
            // left as they were, so nothing is lost and the conflicts can still be listed.
            migrationBuilder.Sql("""
                INSERT INTO holdings (
                    group_id, cph, assigned_to, assigned_user_id, assigned_user_full_name,
                    assignment_detached_at_utc, created_at_utc, updated_at_utc, version)
                SELECT DISTINCT ON (i.group_id, COALESCE(i.context ->> 'cph', ''))
                       i.group_id,
                       COALESCE(i.context ->> 'cph', ''),
                       NULLIF(BTRIM(i.assigned_to), ''),
                       i.assigned_user_id,
                       i.assigned_user_full_name,
                       i.assignment_detached_at_utc,
                       now(),
                       now(),
                       1
                FROM issues i
                LEFT JOIN LATERAL (
                    SELECT MAX(a.at_utc) AS at_utc
                    FROM audit_entries a
                    WHERE a.issue_id = i.id AND a.kind = 'IssueAssigned'
                ) assigned ON TRUE
                WHERE BTRIM(COALESCE(i.context ->> 'cph', '')) <> ''
                  AND (i.assigned_user_id IS NOT NULL OR BTRIM(COALESCE(i.assigned_to, '')) <> '')
                ORDER BY i.group_id,
                         COALESCE(i.context ->> 'cph', ''),
                         (i.assignment_detached_at_utc IS NOT NULL) DESC,
                         (i.status = 'active' AND i.workflow_status IN ('todo', 'in-progress')) DESC,
                         COALESCE(assigned.at_utc, i.updated_at_utc) DESC,
                         i.id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_issues_outcome_reason",
                table: "issues");

            migrationBuilder.DropTable(
                name: "holding_actions");

            migrationBuilder.DropTable(
                name: "holding_notes");

            migrationBuilder.DropTable(
                name: "holdings");

            migrationBuilder.DropTable(
                name: "coded_values");

            migrationBuilder.DropIndex(
                name: "IX_issues_outcome_reason_id",
                table: "issues");

            migrationBuilder.DropCheckConstraint(
                name: "ck_issues_workflow_status",
                table: "issues");

            migrationBuilder.DropIndex(
                name: "ix_audit_entries_subject",
                table: "audit_entries");

            migrationBuilder.DropColumn(
                name: "outcome_reason",
                table: "issues");

            migrationBuilder.DropColumn(
                name: "outcome_reason_id",
                table: "issues");

            migrationBuilder.DropColumn(
                name: "subject",
                table: "audit_entries");

            migrationBuilder.AddCheckConstraint(
                name: "ck_issues_workflow_status",
                table: "issues",
                sql: "workflow_status IN ('todo', 'in-progress', 'suppressed', 'resolved', 'ignored', 'nonactionable')");
        }
    }
}
