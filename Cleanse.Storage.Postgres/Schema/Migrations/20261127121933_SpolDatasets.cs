using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cleanse.Storage.Postgres.Schema.Migrations
{
    /// <inheritdoc />
    public partial class SpolDatasets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "blob_key",
                table: "import_jobs",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<Guid>(
                name: "dataset_id",
                table: "import_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "users_created",
                table: "import_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "spol_datasets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    row_count = table.Column<int>(type: "integer", nullable: false),
                    expected_rows = table.Column<int>(type: "integer", nullable: true),
                    page_count = table.Column<int>(type: "integer", nullable: true),
                    current_load_id = table.Column<Guid>(type: "uuid", nullable: true),
                    load_started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_loaded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by_id = table.Column<string>(type: "text", nullable: false),
                    created_by_name = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_spol_datasets", x => x.id);
                    table.CheckConstraint("ck_spol_datasets_status", "status IN ('Empty', 'Loading', 'Ready')");
                });

            migrationBuilder.CreateTable(
                name: "spol_dataset_items",
                columns: table => new
                {
                    dataset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<int>(type: "integer", nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_spol_dataset_items", x => new { x.dataset_id, x.item_id });
                    table.ForeignKey(
                        name: "fk_spol_dataset_items_dataset",
                        column: x => x.dataset_id,
                        principalTable: "spol_datasets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_import_jobs_dataset",
                table: "import_jobs",
                column: "dataset_id",
                filter: "dataset_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_spol_datasets_created",
                table: "spol_datasets",
                columns: new[] { "created_at_utc", "id" },
                descending: new bool[0]);

            migrationBuilder.AddForeignKey(
                name: "fk_import_jobs_dataset",
                table: "import_jobs",
                column: "dataset_id",
                principalTable: "spol_datasets",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_import_jobs_dataset",
                table: "import_jobs");

            migrationBuilder.DropTable(
                name: "spol_dataset_items");

            migrationBuilder.DropTable(
                name: "spol_datasets");

            migrationBuilder.DropIndex(
                name: "ix_import_jobs_dataset",
                table: "import_jobs");

            migrationBuilder.DropColumn(
                name: "dataset_id",
                table: "import_jobs");

            migrationBuilder.DropColumn(
                name: "users_created",
                table: "import_jobs");

            migrationBuilder.AlterColumn<string>(
                name: "blob_key",
                table: "import_jobs",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
