using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Mashil.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class P2Normalization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "KindCode",
                table: "toilets",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Other");

            migrationBuilder.AddColumn<string>(
                name: "NormalizerVersion",
                table: "toilets",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScheduleStatus",
                table: "toilets",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.CreateTable(
                name: "opening_schedules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ToiletId = table.Column<long>(type: "bigint", nullable: false),
                    DayType = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    OpenMinute = table.Column<int>(type: "integer", nullable: true),
                    CloseMinute = table.Column<int>(type: "integer", nullable: true),
                    IsClosed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_opening_schedules", x => x.Id);
                    table.CheckConstraint("CK_opening_schedules_Closed_or_Times", "\"IsClosed\" OR (\"OpenMinute\" IS NOT NULL AND \"CloseMinute\" IS NOT NULL)");
                    table.CheckConstraint("CK_opening_schedules_Minutes", "\"OpenMinute\" BETWEEN 0 AND 1440 AND \"CloseMinute\" BETWEEN 0 AND 1440");
                    table.ForeignKey(
                        name: "FK_opening_schedules_toilets_ToiletId",
                        column: x => x.ToiletId,
                        principalTable: "toilets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "value_mappings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FieldName = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RawKey = table.Column<string>(type: "text", nullable: false),
                    CanonicalJson = table.Column<string>(type: "jsonb", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_value_mappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_value_mappings_user_profiles_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "user_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "normalization_issues",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ToiletId = table.Column<long>(type: "bigint", nullable: false),
                    FieldName = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Severity = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    RawValue = table.Column<string>(type: "text", nullable: true),
                    Note = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ResolvedByMappingId = table.Column<long>(type: "bigint", nullable: true),
                    ResolvedById = table.Column<Guid>(type: "uuid", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_normalization_issues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_normalization_issues_toilets_ToiletId",
                        column: x => x.ToiletId,
                        principalTable: "toilets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_normalization_issues_user_profiles_ResolvedById",
                        column: x => x.ResolvedById,
                        principalTable: "user_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_normalization_issues_value_mappings_ResolvedByMappingId",
                        column: x => x.ResolvedByMappingId,
                        principalTable: "value_mappings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_normalization_issues_FieldName_RawValue",
                table: "normalization_issues",
                columns: new[] { "FieldName", "RawValue" });

            migrationBuilder.CreateIndex(
                name: "IX_normalization_issues_ResolvedById",
                table: "normalization_issues",
                column: "ResolvedById");

            migrationBuilder.CreateIndex(
                name: "IX_normalization_issues_ResolvedByMappingId",
                table: "normalization_issues",
                column: "ResolvedByMappingId");

            migrationBuilder.CreateIndex(
                name: "IX_normalization_issues_Status_Type",
                table: "normalization_issues",
                columns: new[] { "Status", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_normalization_issues_ToiletId",
                table: "normalization_issues",
                column: "ToiletId");

            migrationBuilder.CreateIndex(
                name: "IX_opening_schedules_ToiletId_DayType",
                table: "opening_schedules",
                columns: new[] { "ToiletId", "DayType" });

            migrationBuilder.CreateIndex(
                name: "IX_value_mappings_CreatedById",
                table: "value_mappings",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_value_mappings_FieldName_RawKey",
                table: "value_mappings",
                columns: new[] { "FieldName", "RawKey" },
                unique: true,
                filter: "\"IsActive\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "normalization_issues");

            migrationBuilder.DropTable(
                name: "opening_schedules");

            migrationBuilder.DropTable(
                name: "value_mappings");

            migrationBuilder.DropColumn(
                name: "KindCode",
                table: "toilets");

            migrationBuilder.DropColumn(
                name: "NormalizerVersion",
                table: "toilets");

            migrationBuilder.DropColumn(
                name: "ScheduleStatus",
                table: "toilets");
        }
    }
}
