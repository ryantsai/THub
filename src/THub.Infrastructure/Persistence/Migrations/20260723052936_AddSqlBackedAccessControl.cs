using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSqlBackedAccessControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // THub is unreleased. Existing fixed-role editor grants are intentionally discarded
            // instead of being guessed into the new custom-role authorization model.
            migrationBuilder.Sql("DELETE FROM [thub].[PublicationGrants];");

            migrationBuilder.DropIndex(
                name: "IX_PublicationGrants_PublicationId_Role",
                schema: "thub",
                table: "PublicationGrants");

            migrationBuilder.DropColumn(
                name: "Role",
                schema: "thub",
                table: "PublicationGrants");

            migrationBuilder.AddColumn<Guid>(
                name: "RoleId",
                schema: "thub",
                table: "PublicationGrants",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "AccessRoles",
                schema: "thub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SystemRole = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccessResourceGrants",
                schema: "thub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Permission = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessResourceGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessResourceGrants_AccessRoles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "thub",
                        principalTable: "AccessRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AccessRoleAssignments",
                schema: "thub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrincipalKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    PrincipalName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    NormalizedPrincipalName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRoleAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessRoleAssignments_AccessRoles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "thub",
                        principalTable: "AccessRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AccessRolePermissions",
                schema: "thub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Permission = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRolePermissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessRolePermissions_AccessRoles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "thub",
                        principalTable: "AccessRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                schema: "thub",
                table: "AccessRoles",
                columns: new[] { "Id", "Name", "Description", "SystemRole", "CreatedAtUtc", "CreatedBy" },
                values: new object[,]
                {
                    {
                        new Guid("10000000-0000-0000-0000-000000000001"),
                        "System Administrator",
                        "Unrestricted platform and resource access.",
                        "SystemAdministrator",
                        new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero),
                        "migration"
                    },
                    {
                        new Guid("10000000-0000-0000-0000-000000000002"),
                        "Developer",
                        "Create, edit, publish, execute, and monitor workflows.",
                        "Developer",
                        new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero),
                        "migration"
                    }
                });

            migrationBuilder.InsertData(
                schema: "thub",
                table: "AccessRolePermissions",
                columns: new[] { "Id", "RoleId", "Permission" },
                values: new object[,]
                {
                    { new Guid("11000000-0000-0000-0000-000000000001"), new Guid("10000000-0000-0000-0000-000000000002"), "workflow.view" },
                    { new Guid("11000000-0000-0000-0000-000000000002"), new Guid("10000000-0000-0000-0000-000000000002"), "workflow.create" },
                    { new Guid("11000000-0000-0000-0000-000000000003"), new Guid("10000000-0000-0000-0000-000000000002"), "workflow.edit" },
                    { new Guid("11000000-0000-0000-0000-000000000004"), new Guid("10000000-0000-0000-0000-000000000002"), "workflow.publish" },
                    { new Guid("11000000-0000-0000-0000-000000000005"), new Guid("10000000-0000-0000-0000-000000000002"), "workflow.execute" },
                    { new Guid("11000000-0000-0000-0000-000000000006"), new Guid("10000000-0000-0000-0000-000000000002"), "run.view" },
                    { new Guid("11000000-0000-0000-0000-000000000007"), new Guid("10000000-0000-0000-0000-000000000002"), "schedule.manage" },
                    { new Guid("11000000-0000-0000-0000-000000000008"), new Guid("10000000-0000-0000-0000-000000000002"), "connection.view" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationGrants_PublicationId_RoleId",
                schema: "thub",
                table: "PublicationGrants",
                columns: new[] { "PublicationId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicationGrants_RoleId",
                schema: "thub",
                table: "PublicationGrants",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessResourceGrants_ResourceKind_ResourceId_Permission",
                schema: "thub",
                table: "AccessResourceGrants",
                columns: new[] { "ResourceKind", "ResourceId", "Permission" });

            migrationBuilder.CreateIndex(
                name: "IX_AccessResourceGrants_RoleId_ResourceKind_ResourceId_Permission",
                schema: "thub",
                table: "AccessResourceGrants",
                columns: new[] { "RoleId", "ResourceKind", "ResourceId", "Permission" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessRoleAssignments_RoleId_PrincipalKind_NormalizedPrincipalName",
                schema: "thub",
                table: "AccessRoleAssignments",
                columns: new[] { "RoleId", "PrincipalKind", "NormalizedPrincipalName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessRolePermissions_RoleId_Permission",
                schema: "thub",
                table: "AccessRolePermissions",
                columns: new[] { "RoleId", "Permission" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessRoles_Name",
                schema: "thub",
                table: "AccessRoles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessRoles_SystemRole",
                schema: "thub",
                table: "AccessRoles",
                column: "SystemRole",
                unique: true,
                filter: "[SystemRole] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_PublicationGrants_AccessRoles_RoleId",
                schema: "thub",
                table: "PublicationGrants",
                column: "RoleId",
                principalSchema: "thub",
                principalTable: "AccessRoles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PublicationGrants_AccessRoles_RoleId",
                schema: "thub",
                table: "PublicationGrants");

            migrationBuilder.DropTable(
                name: "AccessResourceGrants",
                schema: "thub");

            migrationBuilder.DropTable(
                name: "AccessRoleAssignments",
                schema: "thub");

            migrationBuilder.DropTable(
                name: "AccessRolePermissions",
                schema: "thub");

            migrationBuilder.DropTable(
                name: "AccessRoles",
                schema: "thub");

            migrationBuilder.DropIndex(
                name: "IX_PublicationGrants_PublicationId_RoleId",
                schema: "thub",
                table: "PublicationGrants");

            migrationBuilder.DropIndex(
                name: "IX_PublicationGrants_RoleId",
                schema: "thub",
                table: "PublicationGrants");

            migrationBuilder.DropColumn(
                name: "RoleId",
                schema: "thub",
                table: "PublicationGrants");

            migrationBuilder.AddColumn<string>(
                name: "Role",
                schema: "thub",
                table: "PublicationGrants",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationGrants_PublicationId_Role",
                schema: "thub",
                table: "PublicationGrants",
                columns: new[] { "PublicationId", "Role" },
                unique: true);
        }
    }
}
