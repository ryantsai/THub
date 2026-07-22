using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenConnectionMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsEnabled",
                schema: "thub",
                table: "Connections",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                schema: "thub",
                table: "Connections",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAtUtc",
                schema: "thub",
                table: "Connections",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE [thub].[Connections]
                SET [UpdatedAtUtc] = [CreatedAtUtc]
                WHERE [UpdatedAtUtc] IS NULL;
                """);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "UpdatedAtUtc",
                schema: "thub",
                table: "Connections",
                type: "datetimeoffset",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "datetimeoffset",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Connections_Kind_IsEnabled",
                schema: "thub",
                table: "Connections",
                columns: new[] { "Kind", "IsEnabled" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Connections_Kind_IsEnabled",
                schema: "thub",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "IsEnabled",
                schema: "thub",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "thub",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                schema: "thub",
                table: "Connections");
        }
    }
}
