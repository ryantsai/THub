using Microsoft.EntityFrameworkCore;
using THub.Application.Auditing;
using THub.Domain.Auditing;
using THub.Domain.Connections;
using THub.Infrastructure.Auditing;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Tests;

public sealed class AuditRecordFactoryTests
{
    [Fact]
    public void AddedControlPlaneEntityProducesPayloadFreeAuditMetadata()
    {
        var options = new DbContextOptionsBuilder<THubDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=THub.Audit.Unit;Integrated Security=true")
            .Options;
        using var db = new THubDbContext(options);
        var connection = new DataConnection(
            "Warehouse",
            ConnectionKind.SqlServer,
            """{"schemaVersion":1,"server":"sql01"}""",
            "DOMAIN\\owner",
            new DateTimeOffset(2026, 7, 24, 1, 0, 0, TimeSpan.Zero));
        db.Connections.Add(connection);

        using var scope = AuditContext.Push(AuditActorKind.User, "DOMAIN\\operator");
        var records = AuditRecordFactory.Create(
            db.ChangeTracker,
            new DateTimeOffset(2026, 7, 24, 2, 0, 0, TimeSpan.Zero));

        var record = Assert.Single(records);
        Assert.Equal("connection.created", record.Action);
        Assert.Equal("connection", record.ResourceType);
        Assert.Equal(connection.Id.ToString("D"), record.ResourceIdentifier);
        Assert.Equal("DOMAIN\\operator", record.ActorIdentifier);
        Assert.DoesNotContain("sql01", record.ActorIdentifier);
    }
}
