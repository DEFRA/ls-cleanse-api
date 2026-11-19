using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cleanse.Storage.Postgres.Schema.Migrations
{
    /// <inheritdoc />
    public partial class IssueCphGroupIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // An expression index is not part of the EF model, so it is raw SQL.
            // Serves the grouped search (GET /v1/issues/by-cph): its GROUP BY and its fetch of a page of CPHs.
            migrationBuilder.Sql("""
                CREATE INDEX ix_issues_group_cph ON issues (group_id, (COALESCE(context ->> 'cph', '')));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX ix_issues_group_cph;");
        }
    }
}
