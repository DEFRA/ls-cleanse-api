using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cleanse.Storage.Postgres.Schema.Migrations
{
    /// <inheritdoc />
    public partial class ServiceSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "service_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    minimum_issue_age_days = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_settings", x => x.id);
                    table.CheckConstraint("ck_service_settings_minimum_issue_age_days", "minimum_issue_age_days >= 0");
                    table.CheckConstraint("ck_service_settings_one_row", "id = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "service_settings");
        }
    }
}
