using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace THub.Infrastructure.Persistence.Migrations;

[DbContext(typeof(THubDbContext))]
[Migration("20260724190000_AddPersistentAuditTrail")]
public sealed class AddPersistentAuditTrail : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AuditRecords",
            schema: "thub",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                ActorKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                ActorIdentifier = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                Source = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                Outcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                ResourceIdentifier = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                CorrelationIdentifier = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuditRecords", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AuditRecords_ActorIdentifier_OccurredAtUtc",
            schema: "thub",
            table: "AuditRecords",
            columns: new[] { "ActorIdentifier", "OccurredAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_AuditRecords_Action_OccurredAtUtc",
            schema: "thub",
            table: "AuditRecords",
            columns: new[] { "Action", "OccurredAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_AuditRecords_OccurredAtUtc",
            schema: "thub",
            table: "AuditRecords",
            column: "OccurredAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_AuditRecords_ResourceType_ResourceIdentifier_OccurredAtUtc",
            schema: "thub",
            table: "AuditRecords",
            columns: new[] { "ResourceType", "ResourceIdentifier", "OccurredAtUtc" });

        migrationBuilder.Sql(
            """
            EXEC(N'
            CREATE TRIGGER [thub].[TR_AuditRecords_AppendOnly]
            ON [thub].[AuditRecords]
            INSTEAD OF UPDATE, DELETE
            AS
            BEGIN
                SET NOCOUNT ON;
                THROW 51000, ''Audit records are append-only.'', 1;
            END');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AuditRecords",
            schema: "thub");
    }
}
