using Microsoft.EntityFrameworkCore;
using THub.Domain.Alerts;
using THub.Domain.Runs;
using THub.Domain.Workflows;
using THub.Infrastructure.Alerts;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Tests;

public sealed class SqlAlertDeliveryStoreIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConcurrentClaims_RespectPersistedProfileConcurrencyLimit()
    {
        var databaseName = $"THub_AlertClaims_{Guid.NewGuid():N}";
        var connectionString =
            $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Integrated Security=true;Encrypt=false";
        var options = new DbContextOptionsBuilder<THubDbContext>()
            .UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure())
            .Options;
        var factory = new TestDbContextFactory(options);

        try
        {
            await using (var setup = factory.CreateDbContext())
            {
                await setup.Database.MigrateAsync();
                await SeedTwoDeliveriesAsync(setup);
            }

            var store = new SqlAlertDeliveryStore(factory);
            var claimedAtUtc = DateTimeOffset.UtcNow;
            var claims = await Task.WhenAll(
                store.TryClaimNextAsync(
                    "integration-worker-a",
                    claimedAtUtc,
                    TimeSpan.FromMinutes(2),
                    CancellationToken.None),
                store.TryClaimNextAsync(
                    "integration-worker-b",
                    claimedAtUtc,
                    TimeSpan.FromMinutes(2),
                    CancellationToken.None));

            Assert.Single(claims, claim => claim is not null);

            await using var verification = factory.CreateDbContext();
            Assert.Equal(
                1,
                await verification.AlertDeliveries.CountAsync(
                    delivery => delivery.Status == AlertDeliveryStatus.Sending));
            Assert.Equal(
                1,
                await verification.AlertDeliveries.CountAsync(
                    delivery => delivery.Status == AlertDeliveryStatus.Pending));
        }
        finally
        {
            await using var cleanup = factory.CreateDbContext();
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    private static async Task SeedTwoDeliveriesAsync(THubDbContext db)
    {
        var createdAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var graphJson = "{}";
        var workflow = new WorkflowDefinition(
            "Email claim integration workflow",
            "integration-test",
            graphJson,
            createdAtUtc);
        var version = new WorkflowVersion(
            workflow.Id,
            1,
            1,
            graphJson,
            WorkflowVersion.ComputeChecksum(graphJson),
            "integration-test",
            createdAtUtc);
        var profile = new EmailDeliveryProfile(
            "Email claim integration profile",
            "smtp.example.test",
            587,
            EmailTransportSecurity.StartTlsRequired,
            "thub@example.test",
            ["example.test"],
            "integration-test",
            createdAtUtc,
            limits: new EmailDeliveryLimits(20, 200, 20_000, 1));
        var rule = new WorkflowAlertRule(
            workflow.Id,
            profile.Id,
            "Failure notification",
            WorkflowAlertTriggers.RunFailed,
            ["operator@example.test"],
            new EmailTemplate("Workflow failed", "Inspect the run history."),
            "integration-test",
            createdAtUtc);
        var firstRun = new WorkflowRun(
            workflow.Id,
            version.Id,
            version.Version,
            "integration-test",
            createdAtUtc);
        var secondRun = new WorkflowRun(
            workflow.Id,
            version.Id,
            version.Version,
            "integration-test",
            createdAtUtc.AddSeconds(1));
        var message = new EmailMessage(
            ["operator@example.test"],
            "Workflow failed",
            "Inspect the run history.");
        var firstDelivery = AlertDelivery.ForWorkflowRule(
            rule.Id,
            firstRun.Id,
            profile.Id,
            AlertDeliveryEvent.RunFailed,
            message,
            createdAtUtc.AddSeconds(2));
        var secondDelivery = AlertDelivery.ForWorkflowRule(
            rule.Id,
            secondRun.Id,
            profile.Id,
            AlertDeliveryEvent.RunFailed,
            message,
            createdAtUtc.AddSeconds(3));

        db.AddRange(
            workflow,
            version,
            profile,
            rule,
            firstRun,
            secondRun,
            firstDelivery,
            secondDelivery);
        await db.SaveChangesAsync();
    }

    private sealed class TestDbContextFactory(DbContextOptions<THubDbContext> options)
        : IDbContextFactory<THubDbContext>
    {
        public THubDbContext CreateDbContext() => new(options);
    }
}
