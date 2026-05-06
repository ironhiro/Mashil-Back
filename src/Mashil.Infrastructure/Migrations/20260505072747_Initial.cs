using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Mashil.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.CreateTable(
                name: "change_requests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RequesterId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetToiletId = table.Column<long>(type: "bigint", nullable: true),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ReviewedById = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewNote = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_change_requests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "comments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToiletId = table.Column<long>(type: "bigint", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Hidden = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_comments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ratings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToiletId = table.Column<long>(type: "bigint", nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ratings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "toilets",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ManagementNo = table.Column<string>(type: "text", nullable: false),
                    LocalGovCode = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    RoadAddress = table.Column<string>(type: "text", nullable: true),
                    LotAddress = table.Column<string>(type: "text", nullable: true),
                    AddressSource = table.Column<string>(type: "text", nullable: false),
                    Latitude = table.Column<double>(type: "double precision", nullable: true),
                    Longitude = table.Column<double>(type: "double precision", nullable: true),
                    Location = table.Column<Point>(type: "geography (Point, 4326)", nullable: true),
                    CoordinateSource = table.Column<string>(type: "text", nullable: false),
                    MaleStallCount = table.Column<int>(type: "integer", nullable: false),
                    MaleUrinalCount = table.Column<int>(type: "integer", nullable: false),
                    MaleAccessibleStallCount = table.Column<int>(type: "integer", nullable: false),
                    MaleAccessibleUrinalCount = table.Column<int>(type: "integer", nullable: false),
                    MaleChildStallCount = table.Column<int>(type: "integer", nullable: false),
                    MaleChildUrinalCount = table.Column<int>(type: "integer", nullable: false),
                    FemaleStallCount = table.Column<int>(type: "integer", nullable: false),
                    FemaleAccessibleStallCount = table.Column<int>(type: "integer", nullable: false),
                    FemaleChildStallCount = table.Column<int>(type: "integer", nullable: false),
                    ManagementOrg = table.Column<string>(type: "text", nullable: true),
                    Phone = table.Column<string>(type: "text", nullable: true),
                    OpenHours = table.Column<string>(type: "text", nullable: true),
                    OpenHoursDetail = table.Column<string>(type: "text", nullable: true),
                    InstalledYearMonth = table.Column<string>(type: "text", nullable: true),
                    RemodeledYearMonth = table.Column<string>(type: "text", nullable: true),
                    OwnershipKind = table.Column<string>(type: "text", nullable: true),
                    WasteType = table.Column<string>(type: "text", nullable: true),
                    SafetyFacilityRequired = table.Column<bool>(type: "boolean", nullable: true),
                    EmergencyBellInstalled = table.Column<bool>(type: "boolean", nullable: true),
                    EmergencyBellLocation = table.Column<string>(type: "text", nullable: true),
                    EntranceCctv = table.Column<bool>(type: "boolean", nullable: true),
                    DiaperChangerAvailable = table.Column<bool>(type: "boolean", nullable: true),
                    DiaperChangerLocation = table.Column<string>(type: "text", nullable: true),
                    DataReferenceDate = table.Column<DateOnly>(type: "date", nullable: true),
                    DataUpdateKind = table.Column<string>(type: "text", nullable: true),
                    DataUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastModifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_toilets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "user_profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    Role = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_profiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_change_requests_RequesterId",
                table: "change_requests",
                column: "RequesterId");

            migrationBuilder.CreateIndex(
                name: "IX_change_requests_Status",
                table: "change_requests",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_comments_ToiletId",
                table: "comments",
                column: "ToiletId");

            migrationBuilder.CreateIndex(
                name: "IX_comments_ToiletId_Hidden",
                table: "comments",
                columns: new[] { "ToiletId", "Hidden" });

            migrationBuilder.CreateIndex(
                name: "IX_ratings_ToiletId",
                table: "ratings",
                column: "ToiletId");

            migrationBuilder.CreateIndex(
                name: "IX_ratings_UserId_ToiletId",
                table: "ratings",
                columns: new[] { "UserId", "ToiletId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_toilets_Location",
                table: "toilets",
                column: "Location")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "IX_toilets_ManagementNo",
                table: "toilets",
                column: "ManagementNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_profiles_Email",
                table: "user_profiles",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "change_requests");

            migrationBuilder.DropTable(
                name: "comments");

            migrationBuilder.DropTable(
                name: "ratings");

            migrationBuilder.DropTable(
                name: "toilets");

            migrationBuilder.DropTable(
                name: "user_profiles");
        }
    }
}
