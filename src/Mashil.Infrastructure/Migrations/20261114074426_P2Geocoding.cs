using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mashil.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class P2Geocoding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "geocode_cache",
                columns: table => new
                {
                    QueryKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Latitude = table.Column<double>(type: "double precision", nullable: true),
                    Longitude = table.Column<double>(type: "double precision", nullable: true),
                    MatchedAddress = table.Column<string>(type: "text", nullable: true),
                    MatchedSido = table.Column<string>(type: "text", nullable: true),
                    CandidateCount = table.Column<int>(type: "integer", nullable: false),
                    GeocodedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_geocode_cache", x => x.QueryKey);
                });

            migrationBuilder.CreateTable(
                name: "geocode_daily_usage",
                columns: table => new
                {
                    UsageDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Calls = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_geocode_daily_usage", x => x.UsageDate);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "geocode_cache");

            migrationBuilder.DropTable(
                name: "geocode_daily_usage");
        }
    }
}
