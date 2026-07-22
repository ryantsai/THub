using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using THub.Domain.Workflows;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Tests;

public sealed class WorkflowVersionBackfillMigrationTests
{
    private const string PreviousMigration = "20260722155025_AdoptQuartzScheduling";
    private const string BackfillMigration = "20260722170020_AddDurableExecutionAlertsAndPublications";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CurrentLegacyVersionBackfillsDeterministicIdentityAndChecksum()
    {
        var fixture = CreateFixture();
        try
        {
            var workflowId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            const int versionNumber = 3;
            const string legacyGraph =
                "{\"nodes\":[{\"id\":\"source\",\"kind\":\"SqlSource\",\"name\":\"\u8cc7\u6599\u6e90\",\"x\":0,\"y\":0,\"settings\":{}}],\"edges\":[]}";
            var timestamp = DateTimeOffset.UtcNow.AddMinutes(-5);

            await using (var setup = fixture.Factory.CreateDbContext())
            {
                await setup.GetService<IMigrator>().MigrateAsync(PreviousMigration);
                await InsertLegacyWorkflowAsync(
                    setup,
                    workflowId,
                    versionNumber,
                    legacyGraph,
                    timestamp);
                await InsertLegacyRunAsync(
                    setup,
                    runId,
                    workflowId,
                    versionNumber,
                    timestamp);

                await setup.GetService<IMigrator>().MigrateAsync(BackfillMigration);
            }

            await using var verification = fixture.Factory.CreateDbContext();
            var snapshot = await verification.WorkflowVersions.AsNoTracking().SingleAsync();
            var run = await verification.WorkflowRuns.AsNoTracking().SingleAsync();
            var workflow = await verification.Workflows.AsNoTracking().SingleAsync();
            var expectedVersionId = WorkflowVersion.CreateId(workflowId, versionNumber);

            Assert.Equal(expectedVersionId, snapshot.Id);
            Assert.Equal(expectedVersionId, run.WorkflowVersionId);
            Assert.Equal(expectedVersionId, workflow.PublishedVersionId);
            Assert.Equal(versionNumber, workflow.PublishedVersionNumber);
            Assert.Contains("\"schemaVersion\":1", snapshot.GraphJson, StringComparison.Ordinal);
            Assert.Contains("\u8cc7\u6599\u6e90", snapshot.GraphJson, StringComparison.Ordinal);
            Assert.Equal(WorkflowVersion.ComputeChecksum(snapshot.GraphJson), snapshot.Checksum);
        }
        finally
        {
            await DeleteDatabaseAsync(fixture.Factory);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task HistoricalLegacyRunFailsMigrationInsteadOfReceivingCurrentGraph()
    {
        var fixture = CreateFixture();
        try
        {
            var workflowId = Guid.NewGuid();
            var timestamp = DateTimeOffset.UtcNow.AddMinutes(-5);

            await using var setup = fixture.Factory.CreateDbContext();
            await setup.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            await InsertLegacyWorkflowAsync(
                setup,
                workflowId,
                version: 3,
                "{\"nodes\":[],\"edges\":[]}",
                timestamp);
            await InsertLegacyRunAsync(
                setup,
                Guid.NewGuid(),
                workflowId,
                version: 2,
                timestamp);

            var exception = await Assert.ThrowsAnyAsync<Exception>(
                () => setup.GetService<IMigrator>().MigrateAsync(BackfillMigration));

            Assert.Contains(
                "cannot reconstruct immutable snapshots for legacy runs",
                exception.ToString(),
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                BackfillMigration,
                await setup.Database.GetAppliedMigrationsAsync());
        }
        finally
        {
            await DeleteDatabaseAsync(fixture.Factory);
        }
    }

    private static async Task InsertLegacyWorkflowAsync(
        THubDbContext db,
        Guid workflowId,
        int version,
        string graphJson,
        DateTimeOffset timestamp)
    {
        _ = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO [thub].[Workflows]
                ([Id], [Name], [Description], [Owner], [Status], [Version], [GraphJson],
                 [CronExpression], [TimeZoneId], [NextRunAtUtc], [CreatedAtUtc], [UpdatedAtUtc])
            VALUES
                ({workflowId}, N'Legacy workflow', N'', N'integration-test', N'Published', {version},
                 {graphJson}, NULL, N'UTC', NULL, {timestamp}, {timestamp});
            """);
    }

    private static async Task InsertLegacyRunAsync(
        THubDbContext db,
        Guid runId,
        Guid workflowId,
        int version,
        DateTimeOffset timestamp)
    {
        _ = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO [thub].[WorkflowRuns]
                ([Id], [WorkflowId], [WorkflowVersion], [Status], [TriggeredBy], [QueuedAtUtc],
                 [StartedAtUtc], [CompletedAtUtc], [ErrorMessage], [ScheduledForUtc])
            VALUES
                ({runId}, {workflowId}, {version}, N'Succeeded', N'integration-test', {timestamp},
                 {timestamp}, {timestamp}, NULL, NULL);
            """);
    }

    private static TestFixture CreateFixture()
    {
        var databaseName = $"THub_VersionBackfill_{Guid.NewGuid():N}";
        var connectionString =
            $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Integrated Security=true;Encrypt=false";
        var options = new DbContextOptionsBuilder<THubDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        return new(new TestDbContextFactory(options));
    }

    private static async Task DeleteDatabaseAsync(IDbContextFactory<THubDbContext> factory)
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
}
