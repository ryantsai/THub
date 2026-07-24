using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(THubDbContext))]
[Migration("20260724230000_AddSoftDeletedConnections")]
public sealed class AddSoftDeletedConnections : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "DeletedAtUtc",
            schema: "thub",
            table: "Connections",
            type: "datetimeoffset",
            nullable: true);

        migrationBuilder.DropIndex(
            name: "IX_Connections_Name",
            schema: "thub",
            table: "Connections");

        migrationBuilder.CreateIndex(
            name: "IX_Connections_Name",
            schema: "thub",
            table: "Connections",
            column: "Name",
            unique: true,
            filter: "[DeletedAtUtc] IS NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Connections_Name",
            schema: "thub",
            table: "Connections");

        migrationBuilder.DropColumn(
            name: "DeletedAtUtc",
            schema: "thub",
            table: "Connections");

        migrationBuilder.CreateIndex(
            name: "IX_Connections_Name",
            schema: "thub",
            table: "Connections",
            column: "Name",
            unique: true);
    }
}
