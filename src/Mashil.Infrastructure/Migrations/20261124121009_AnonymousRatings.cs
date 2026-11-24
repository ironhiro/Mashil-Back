using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mashil.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AnonymousRatings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                table: "ratings",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "DeviceKeyHash",
                table: "ratings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ratings_DeviceKeyHash_ToiletId",
                table: "ratings",
                columns: new[] { "DeviceKeyHash", "ToiletId" },
                unique: true,
                filter: "\"DeviceKeyHash\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ratings_Owner",
                table: "ratings",
                sql: "(\"UserId\" IS NULL) <> (\"DeviceKeyHash\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 되돌리면 UserId 가 다시 NOT NULL: 로그인 없이 매긴 별점은 주인을 옮길 곳이 없어 지운다
            migrationBuilder.Sql("DELETE FROM ratings WHERE \"UserId\" IS NULL;");

            migrationBuilder.DropIndex(
                name: "IX_ratings_DeviceKeyHash_ToiletId",
                table: "ratings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ratings_Owner",
                table: "ratings");

            migrationBuilder.DropColumn(
                name: "DeviceKeyHash",
                table: "ratings");

            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                table: "ratings",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
