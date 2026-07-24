using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(THubDbContext))]
[Migration("20260724120000_AddPublicationApplyConnection")]
public partial class AddPublicationApplyConnection : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "ApplyConnectionId",
            schema: "thub",
            table: "PublicationVersions",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_PublicationVersions_ApplyConnectionId",
            schema: "thub",
            table: "PublicationVersions",
            column: "ApplyConnectionId");

        migrationBuilder.AddForeignKey(
            name: "FK_PublicationVersions_Connections_ApplyConnectionId",
            schema: "thub",
            table: "PublicationVersions",
            column: "ApplyConnectionId",
            principalSchema: "thub",
            principalTable: "Connections",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_PublicationVersions_Connections_ApplyConnectionId",
            schema: "thub",
            table: "PublicationVersions");

        migrationBuilder.DropIndex(
            name: "IX_PublicationVersions_ApplyConnectionId",
            schema: "thub",
            table: "PublicationVersions");

        migrationBuilder.DropColumn(
            name: "ApplyConnectionId",
            schema: "thub",
            table: "PublicationVersions");
    }
}
