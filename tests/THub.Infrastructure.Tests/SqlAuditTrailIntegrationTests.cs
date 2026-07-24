using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using THub.Application.Auditing;
using THub.Domain.Auditing;
using THub.Domain.Connections;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Tests;

public sealed class SqlAuditTrailIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ControlPlaneChangeCommitsAuditAndDatabaseRejectsMutation()
    {
        var databaseName = $"THub_Audit_{Guid.NewGuid():N}";
        var connectionString =
            $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Integrated Security=true;Encrypt=false";
        var options = new DbContextOptionsBuilder<THubDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        try
        {
            await using (var setup = new THubDbContext(options))
            {
                await setup.Database.MigrateAsync();
                using var auditScope = AuditContext.Push(
                    AuditActorKind.User,
                    "DOMAIN\\audit-operator");
                setup.Connections.Add(new DataConnection(
                    "Audit warehouse",
                    ConnectionKind.SqlServer,
                    """{"schemaVersion":1,"server":"sql01"}""",
                    "DOMAIN\\owner"));
                await setup.SaveChangesAsync();
            }

            Guid auditId;
            await using (var verification = new THubDbContext(options))
            {
                var audit = await verification.AuditRecords.AsNoTracking().SingleAsync(record =>
                    record.Action == "connection.created");
                auditId = audit.Id;
                Assert.Equal("DOMAIN\\audit-operator", audit.ActorIdentifier);
                Assert.DoesNotContain("sql01", audit.ActorIdentifier);
            }

            await using var mutation = new THubDbContext(options);
            var exception = await Assert.ThrowsAsync<SqlException>(() =>
                mutation.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM [thub].[AuditRecords] WHERE [Id] = {auditId}"));
            Assert.Equal(51000, exception.Number);
        }
        finally
        {
            await using var cleanup = new THubDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }
}
