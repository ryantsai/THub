using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(THubDbContext))]
[Migration("20260724090000_AddTrustedActions")]
public partial class AddTrustedActions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TrustedActions",
            schema: "thub",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                CredentialReference = table.Column<string>(type: "nvarchar(185)", maxLength: 185, nullable: true),
                IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TrustedActions", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_TrustedActions_Kind_IsEnabled",
            schema: "thub",
            table: "TrustedActions",
            columns: new[] { "Kind", "IsEnabled" });

        migrationBuilder.CreateIndex(
            name: "IX_TrustedActions_Name",
            schema: "thub",
            table: "TrustedActions",
            column: "Name",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "TrustedActions",
            schema: "thub");
    }
}
