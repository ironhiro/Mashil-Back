using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mashil.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HideSourceDuplicates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DuplicateOfId",
                table: "toilets",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_toilets_DuplicateOfId",
                table: "toilets",
                column: "DuplicateOfId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_toilets_DuplicateOfId",
                table: "toilets");

            migrationBuilder.DropColumn(
                name: "DuplicateOfId",
                table: "toilets");
        }
    }
}
