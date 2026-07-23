using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace THub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(THubDbContext))]
[Migration("20260723140000_AddRelationalTargetMutationPermissions")]
public sealed class AddRelationalTargetMutationPermissions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            IF NOT EXISTS (
                SELECT 1
                FROM [thub].[AccessRolePermissions]
                WHERE [RoleId] = '10000000-0000-0000-0000-000000000002'
                  AND [Permission] = N'workflow.target.upsert')
            BEGIN
                INSERT INTO [thub].[AccessRolePermissions] ([Id], [RoleId], [Permission])
                VALUES (
                    '11000000-0000-0000-0000-000000000010',
                    '10000000-0000-0000-0000-000000000002',
                    N'workflow.target.upsert');
            END

            IF NOT EXISTS (
                SELECT 1
                FROM [thub].[AccessRolePermissions]
                WHERE [RoleId] = '10000000-0000-0000-0000-000000000002'
                  AND [Permission] = N'workflow.target.delete')
            BEGIN
                INSERT INTO [thub].[AccessRolePermissions] ([Id], [RoleId], [Permission])
                VALUES (
                    '11000000-0000-0000-0000-000000000011',
                    '10000000-0000-0000-0000-000000000002',
                    N'workflow.target.delete');
            END
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DELETE FROM [thub].[AccessRolePermissions]
            WHERE [Id] IN (
                '11000000-0000-0000-0000-000000000010',
                '11000000-0000-0000-0000-000000000011');
            """);
    }
}
