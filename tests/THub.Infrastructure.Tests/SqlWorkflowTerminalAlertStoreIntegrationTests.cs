using Microsoft.EntityFrameworkCore;
using THub.Application.Alerts;
using THub.Domain.Alerts;
using THub.Domain.Runs;
using THub.Domain.Workflows;
using THub.Infrastructure.Alerts;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Tests;

public sealed class SqlWorkflowTerminalAlertStoreIntegrationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 20, 30, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task QueuedCancellation_CommitsRunAndMatchingAlertInOneTransaction()
    {
        var fixture = CreateFixture();
        try
        {
            WorkflowRun run;
            await using (var setup = fixture.Factory.CreateDbContext())
            {
                await setup.Database.MigrateAsync();
                run = await SeedRunAndCancellationRuleAsync(setup);
            }

            Assert.True(run.RequestCancellation("DOMAIN\\operator", Now));
            var service = new WorkflowTerminalAlertService(
                new SqlWorkflowTerminalAlertStore(fixture.Factory),
                new FixedTimeProvider(Now));

            var result = await service.CommitAsync(
                new CommitTerminalRunWithAlertsCommand(
                    run,
                    WorkflowRunStatus.Queued,
                    ExpectedLeaseOwner: null),
                CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal(1, result.Value!.DeliveryCount);
            await using var verification = fixture.Factory.CreateDbContext();
            var storedRun = await verification.WorkflowRuns.SingleAsync();
            var delivery = await verification.AlertDeliveries.SingleAsync();
            Assert.Equal(WorkflowRunStatus.Cancelled, storedRun.Status);
            Assert.Equal(AlertDeliveryEvent.RunCancelled, delivery.Event);
            Assert.Equal(AlertDeliveryStatus.Pending, delivery.Status);
            Assert.Contains("Cancelled", delivery.Message.Subject, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteDatabaseAsync(fixture.Factory);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CommitRejectsPreparedRuleSetThatOmitsCurrentMatchingRule()
    {
        var fixture = CreateFixture();
        try
        {
            WorkflowRun run;
            await using (var setup = fixture.Factory.CreateDbContext())
            {
                await setup.Database.MigrateAsync();
                run = await SeedRunAndCancellationRuleAsync(setup);
            }

            Assert.True(run.RequestCancellation("DOMAIN\\operator", Now));
            var store = new SqlWorkflowTerminalAlertStore(fixture.Factory);

            var status = await store.CommitTerminalRunAsync(
                run,
                WorkflowRunStatus.Queued,
                expectedLeaseOwner: null,
                alerts: [],
                CancellationToken.None);

            Assert.Equal(TerminalAlertCommitStatus.SnapshotChanged, status);
            await using var verification = fixture.Factory.CreateDbContext();
            Assert.Equal(WorkflowRunStatus.Queued, (await verification.WorkflowRuns.SingleAsync()).Status);
            Assert.Empty(await verification.AlertDeliveries.ToListAsync());
        }
        finally
        {
            await DeleteDatabaseAsync(fixture.Factory);
        }
    }

    private static async Task<WorkflowRun> SeedRunAndCancellationRuleAsync(THubDbContext db)
    {
        var createdAtUtc = Now.AddMinutes(-5);
        var graphJson = "{}";
        var workflow = new WorkflowDefinition(
            "Cancellation alert integration workflow",
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
            "Cancellation alert integration profile",
            "smtp.example.test",
            587,
            EmailTransportSecurity.StartTlsRequired,
            "thub@example.test",
            ["example.test"],
            "integration-test",
            createdAtUtc);
        var rule = new WorkflowAlertRule(
            workflow.Id,
            profile.Id,
            "Cancellation notification",
            WorkflowAlertTriggers.RunCancelled,
            ["operator@example.test"],
            new EmailTemplate("{{run.status}} workflow run", "Run {{run.id}} was cancelled."),
            "integration-test",
            createdAtUtc);
        var run = new WorkflowRun(
            workflow.Id,
            version.Id,
            version.Version,
            "integration-test",
            createdAtUtc.AddMinutes(1));

        db.AddRange(workflow, version, profile, rule, run);
        await db.SaveChangesAsync();
        return run;
    }

    private static TestFixture CreateFixture()
    {
        var databaseName = $"THub_TerminalAlerts_{Guid.NewGuid():N}";
        var connectionString =
            $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Integrated Security=true;Encrypt=false";
        var options = new DbContextOptionsBuilder<THubDbContext>()
            .UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure())
            .Options;
        return new TestFixture(new TestDbContextFactory(options));
    }

    private static async Task DeleteDatabaseAsync(TestDbContextFactory factory)
    {
        await using var cleanup = factory.CreateDbContext();
        await cleanup.Database.EnsureDeletedAsync();
    }

    private sealed record TestFixture(TestDbContextFactory Factory);

    private sealed class TestDbContextFactory(DbContextOptions<THubDbContext> options)
        : IDbContextFactory<THubDbContext>
    {
        public THubDbContext CreateDbContext() => new(options);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
