using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace THub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(THubDbContext))]
[Migration("20260723120000_AddWorkflowDeletePermission")]
public sealed class AddWorkflowDeletePermission : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            IF NOT EXISTS (
                SELECT 1
                FROM [thub].[AccessRolePermissions]
                WHERE [RoleId] = '10000000-0000-0000-0000-000000000002'
                  AND [Permission] = N'workflow.delete')
            BEGIN
                INSERT INTO [thub].[AccessRolePermissions] ([Id], [RoleId], [Permission])
                VALUES (
                    '11000000-0000-0000-0000-000000000009',
                    '10000000-0000-0000-0000-000000000002',
                    N'workflow.delete');
            END
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DELETE FROM [thub].[AccessRolePermissions]
            WHERE [Id] = '11000000-0000-0000-0000-000000000009';
            """);
    }
}
