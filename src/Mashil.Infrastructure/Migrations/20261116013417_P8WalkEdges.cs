using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Mashil.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class P8WalkEdges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "walk_edges",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Source = table.Column<long>(type: "bigint", nullable: false),
                    Target = table.Column<long>(type: "bigint", nullable: false),
                    Geom = table.Column<LineString>(type: "geometry (LineString, 4326)", nullable: false),
                    LengthM = table.Column<double>(type: "double precision", nullable: false),
                    CostFactor = table.Column<float>(type: "real", nullable: false),
                    Steps = table.Column<bool>(type: "boolean", nullable: false),
                    Highway = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_walk_edges", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_walk_edges_Geom",
                table: "walk_edges",
                column: "Geom")
                .Annotation("Npgsql:IndexMethod", "gist");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "walk_edges");
        }
    }
}
