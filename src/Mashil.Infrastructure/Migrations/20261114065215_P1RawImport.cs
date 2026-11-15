using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Mashil.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class P1RawImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "toilets",
                type: "text",
                nullable: false,
                defaultValue: "PublicData");

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "toilets",
                type: "text",
                nullable: false,
                defaultValue: "Active");

            migrationBuilder.CreateTable(
                name: "data_sources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Provider = table.Column<string>(type: "text", nullable: false),
                    Format = table.Column<string>(type: "text", nullable: false),
                    Encoding = table.Column<string>(type: "text", nullable: false),
                    InfoUrl = table.Column<string>(type: "text", nullable: true),
                    DownloadUrl = table.Column<string>(type: "text", nullable: true),
                    License = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_data_sources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "toilet_overrides",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ToiletId = table.Column<long>(type: "bigint", nullable: false),
                    FieldName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ValueJson = table.Column<string>(type: "jsonb", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    ChangeRequestId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_toilet_overrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_toilet_overrides_change_requests_ChangeRequestId",
                        column: x => x.ChangeRequestId,
                        principalTable: "change_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_toilet_overrides_toilets_ToiletId",
                        column: x => x.ToiletId,
                        principalTable: "toilets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_toilet_overrides_user_profiles_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "user_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "import_runs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SourceId = table.Column<int>(type: "integer", nullable: false),
                    Trigger = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: true),
                    FileSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    DetectedEncoding = table.Column<string>(type: "text", nullable: true),
                    NormalizerVersion = table.Column<string>(type: "text", nullable: false),
                    TotalRows = table.Column<int>(type: "integer", nullable: false),
                    InsertedRows = table.Column<int>(type: "integer", nullable: false),
                    UpdatedRows = table.Column<int>(type: "integer", nullable: false),
                    UnchangedRows = table.Column<int>(type: "integer", nullable: false),
                    SkippedRows = table.Column<int>(type: "integer", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HeartbeatAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_import_runs_data_sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "data_sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "raw_toilet_records",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SourceId = table.Column<int>(type: "integer", nullable: false),
                    SourceKey = table.Column<string>(type: "text", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    RowHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ToiletId = table.Column<long>(type: "bigint", nullable: true),
                    LastImportRunId = table.Column<long>(type: "bigint", nullable: false),
                    ConsecutiveMissing = table.Column<int>(type: "integer", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_raw_toilet_records", x => x.Id);
                    table.ForeignKey(
                        name: "FK_raw_toilet_records_data_sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "data_sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_raw_toilet_records_import_runs_LastImportRunId",
                        column: x => x.LastImportRunId,
                        principalTable: "import_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_raw_toilet_records_toilets_ToiletId",
                        column: x => x.ToiletId,
                        principalTable: "toilets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "data_sources",
                columns: new[] { "Id", "Code", "DownloadUrl", "Encoding", "Format", "InfoUrl", "IsActive", "License", "Name", "Provider" },
                values: new object[] { 1, "LOCALDATA_PUBLIC_RESTROOM", "https://file.localdata.go.kr/file/download/public_restroom_info/info", "Auto", "CSV", "https://file.localdata.go.kr/file/public_restroom_info/info", true, null, "공중화장실정보 (전국파일)", "LOCALDATA" });

            migrationBuilder.CreateIndex(
                name: "IX_toilets_Status",
                table: "toilets",
                column: "Status");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ratings_Score",
                table: "ratings",
                sql: "\"Score\" BETWEEN 1 AND 5");

            migrationBuilder.CreateIndex(
                name: "IX_comments_UserId",
                table: "comments",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_change_requests_ReviewedById",
                table: "change_requests",
                column: "ReviewedById");

            migrationBuilder.CreateIndex(
                name: "IX_change_requests_TargetToiletId",
                table: "change_requests",
                column: "TargetToiletId");

            migrationBuilder.CreateIndex(
                name: "IX_data_sources_Code",
                table: "data_sources",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_import_runs_SourceId_Status_StartedAt",
                table: "import_runs",
                columns: new[] { "SourceId", "Status", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_raw_toilet_records_LastImportRunId",
                table: "raw_toilet_records",
                column: "LastImportRunId");

            migrationBuilder.CreateIndex(
                name: "IX_raw_toilet_records_SourceId_LastSeenAt",
                table: "raw_toilet_records",
                columns: new[] { "SourceId", "LastSeenAt" });

            migrationBuilder.CreateIndex(
                name: "IX_raw_toilet_records_SourceId_SourceKey",
                table: "raw_toilet_records",
                columns: new[] { "SourceId", "SourceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_raw_toilet_records_ToiletId",
                table: "raw_toilet_records",
                column: "ToiletId");

            migrationBuilder.CreateIndex(
                name: "IX_toilet_overrides_ChangeRequestId",
                table: "toilet_overrides",
                column: "ChangeRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_toilet_overrides_CreatedById",
                table: "toilet_overrides",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_toilet_overrides_ToiletId_FieldName_Source",
                table: "toilet_overrides",
                columns: new[] { "ToiletId", "FieldName", "Source" },
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.AddForeignKey(
                name: "FK_change_requests_toilets_TargetToiletId",
                table: "change_requests",
                column: "TargetToiletId",
                principalTable: "toilets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_change_requests_user_profiles_RequesterId",
                table: "change_requests",
                column: "RequesterId",
                principalTable: "user_profiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_change_requests_user_profiles_ReviewedById",
                table: "change_requests",
                column: "ReviewedById",
                principalTable: "user_profiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_comments_toilets_ToiletId",
                table: "comments",
                column: "ToiletId",
                principalTable: "toilets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_comments_user_profiles_UserId",
                table: "comments",
                column: "UserId",
                principalTable: "user_profiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ratings_toilets_ToiletId",
                table: "ratings",
                column: "ToiletId",
                principalTable: "toilets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ratings_user_profiles_UserId",
                table: "ratings",
                column: "UserId",
                principalTable: "user_profiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_change_requests_toilets_TargetToiletId",
                table: "change_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_change_requests_user_profiles_RequesterId",
                table: "change_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_change_requests_user_profiles_ReviewedById",
                table: "change_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_comments_toilets_ToiletId",
                table: "comments");

            migrationBuilder.DropForeignKey(
                name: "FK_comments_user_profiles_UserId",
                table: "comments");

            migrationBuilder.DropForeignKey(
                name: "FK_ratings_toilets_ToiletId",
                table: "ratings");

            migrationBuilder.DropForeignKey(
                name: "FK_ratings_user_profiles_UserId",
                table: "ratings");

            migrationBuilder.DropTable(
                name: "raw_toilet_records");

            migrationBuilder.DropTable(
                name: "toilet_overrides");

            migrationBuilder.DropTable(
                name: "import_runs");

            migrationBuilder.DropTable(
                name: "data_sources");

            migrationBuilder.DropIndex(
                name: "IX_toilets_Status",
                table: "toilets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ratings_Score",
                table: "ratings");

            migrationBuilder.DropIndex(
                name: "IX_comments_UserId",
                table: "comments");

            migrationBuilder.DropIndex(
                name: "IX_change_requests_ReviewedById",
                table: "change_requests");

            migrationBuilder.DropIndex(
                name: "IX_change_requests_TargetToiletId",
                table: "change_requests");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "toilets");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "toilets");
        }
    }
}
