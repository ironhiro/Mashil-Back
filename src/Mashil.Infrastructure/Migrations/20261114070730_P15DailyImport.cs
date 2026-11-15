using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mashil.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class P15DailyImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MissingRows",
                table: "import_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RemovalCandidates",
                table: "import_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RemovedRows",
                table: "import_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RestoredRows",
                table: "import_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "UX_import_runs_SourceId_Running",
                table: "import_runs",
                column: "SourceId",
                unique: true,
                filter: "\"Status\" = 'Running'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_import_runs_SourceId_Running",
                table: "import_runs");

            migrationBuilder.DropColumn(
                name: "MissingRows",
                table: "import_runs");

            migrationBuilder.DropColumn(
                name: "RemovalCandidates",
                table: "import_runs");

            migrationBuilder.DropColumn(
                name: "RemovedRows",
                table: "import_runs");

            migrationBuilder.DropColumn(
                name: "RestoredRows",
                table: "import_runs");
        }
    }
}
