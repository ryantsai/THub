using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using THub.Application.Execution;
using THub.Domain.Runs;
using THub.Domain.Workflows;
using THub.Infrastructure.Execution;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Tests;

public sealed class SqlWorkflowExecutionEventSinkIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task OwnedLeasePersistsStepLifecycle()
    {
        var fixture = CreateFixture();
        try
        {
            const string leaseOwner = "integration-worker-a";
            var now = DateTimeOffset.UtcNow;
            var runId = await SeedClaimedRunAsync(fixture.Factory, leaseOwner, now);
            var sink = new SqlWorkflowExecutionEventSinkFactory(fixture.Factory)
                .Create(runId, leaseOwner);

            await sink.WriteAsync(
                new WorkflowExecutionEvent(
                    runId,
                    WorkflowExecutionEventKind.NodeStarted,
                    now,
                    "source",
                    Attempt: 1),
                CancellationToken.None);
            await sink.WriteAsync(
                new WorkflowExecutionEvent(
                    runId,
                    WorkflowExecutionEventKind.NodeSucceeded,
                    now.AddSeconds(1),
                    "source",
                    Attempt: 1,
                    new WorkflowNodeProgress(RowsRead: 2, BatchesProcessed: 1, BytesRead: 16)),
                CancellationToken.None);

            await using var verification = fixture.Factory.CreateDbContext();
            var step = await verification.WorkflowStepRuns.AsNoTracking().SingleAsync();
            Assert.Equal(WorkflowStepRunStatus.Succeeded, step.Status);
            Assert.Equal(2, step.RowsRead);
            Assert.Equal(1, step.BatchesProcessed);
            Assert.Equal(16, step.BytesRead);
        }
        finally
        {
            await DeleteDatabaseAsync(fixture.Factory);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task StaleLeaseOwnerCannotPersistStepEvent()
    {
        var fixture = CreateFixture();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var runId = await SeedClaimedRunAsync(fixture.Factory, "current-worker", now);
            var staleSink = new SqlWorkflowExecutionEventSinkFactory(fixture.Factory)
                .Create(runId, "stale-worker");

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await staleSink.WriteAsync(
                    new WorkflowExecutionEvent(
                        runId,
                        WorkflowExecutionEventKind.NodeStarted,
                        now,
                        "source",
                        Attempt: 1),
                    CancellationToken.None));

            Assert.Contains("lease", exception.Message, StringComparison.OrdinalIgnoreCase);
            await using var verification = fixture.Factory.CreateDbContext();
            Assert.Empty(await verification.WorkflowStepRuns.AsNoTracking().ToListAsync());
        }
        finally
        {
            await DeleteDatabaseAsync(fixture.Factory);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LeaseTakeoverWaitsUntilGuardedStepWriteCommits()
    {
        var insertBlocker = new BlockingStepInsertInterceptor();
        var fixture = CreateFixture(insertBlocker);
        try
        {
            const string originalOwner = "integration-worker-a";
            const string replacementOwner = "integration-worker-b";
            var now = DateTimeOffset.UtcNow;
            var runId = await SeedClaimedRunAsync(fixture.Factory, originalOwner, now);
            var sink = new SqlWorkflowExecutionEventSinkFactory(fixture.Factory)
                .Create(runId, originalOwner);
            var writeTask = sink.WriteAsync(
                new WorkflowExecutionEvent(
                    runId,
                    WorkflowExecutionEventKind.NodeStarted,
                    now,
                    "source",
                    Attempt: 1),
                CancellationToken.None).AsTask();

            await insertBlocker.InsertStarted.WaitAsync(TimeSpan.FromSeconds(5));
            var updateStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var takeoverTask = ReplaceLeaseOwnerAsync(
                fixture.ConnectionString,
                runId,
                replacementOwner,
                updateStarted);
            await updateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var completedBeforeRelease = await Task.WhenAny(
                takeoverTask,
                Task.Delay(TimeSpan.FromMilliseconds(250))) == takeoverTask;
            insertBlocker.Release();
            await writeTask;
            Assert.Equal(1, await takeoverTask);

            Assert.False(completedBeforeRelease);
            await using var verification = fixture.Factory.CreateDbContext();
            Assert.Single(await verification.WorkflowStepRuns.AsNoTracking().ToListAsync());
            Assert.Equal(
                replacementOwner,
                (await verification.WorkflowRuns.AsNoTracking().SingleAsync()).LeaseOwner);
        }
        finally
        {
            insertBlocker.Release();
            await DeleteDatabaseAsync(fixture.Factory);
        }
    }

    private static async Task<Guid> SeedClaimedRunAsync(
        IDbContextFactory<THubDbContext> factory,
        string leaseOwner,
        DateTimeOffset now)
    {
        await using var db = factory.CreateDbContext();
        await db.Database.MigrateAsync();
        var graphJson = "{}";
        var workflow = new WorkflowDefinition(
            "Execution event integration workflow",
            "integration-test",
            graphJson,
            now.AddMinutes(-1));
        var version = new WorkflowVersion(
            workflow.Id,
            1,
            1,
            graphJson,
            WorkflowVersion.ComputeChecksum(graphJson),
            "integration-test",
            now.AddMinutes(-1));
        var run = new WorkflowRun(
            workflow.Id,
            version.Id,
            version.Version,
            "integration-test",
            now.AddMinutes(-1));
        Assert.True(run.TryClaim(leaseOwner, now, TimeSpan.FromMinutes(5)));
        db.AddRange(workflow, version, run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private static TestFixture CreateFixture(params IInterceptor[] interceptors)
    {
        var databaseName = $"THub_ExecutionEvents_{Guid.NewGuid():N}";
        var connectionString =
            $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Integrated Security=true;Encrypt=false";
        var optionsBuilder = new DbContextOptionsBuilder<THubDbContext>()
            .UseSqlServer(connectionString);
        if (interceptors.Length > 0)
        {
            _ = optionsBuilder.AddInterceptors(interceptors);
        }

        return new(new TestDbContextFactory(optionsBuilder.Options), connectionString);
    }

    private static async Task<int> ReplaceLeaseOwnerAsync(
        string connectionString,
        Guid runId,
        string leaseOwner,
        TaskCompletionSource<bool> updateStarted)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE [thub].[WorkflowRuns]
            SET [LeaseOwner] = @LeaseOwner
            WHERE [Id] = @WorkflowRunId;
            """;
        command.Parameters.Add(new SqlParameter("@LeaseOwner", leaseOwner));
        command.Parameters.Add(new SqlParameter("@WorkflowRunId", runId));
        updateStarted.TrySetResult(true);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task DeleteDatabaseAsync(IDbContextFactory<THubDbContext> factory)
    {
        await using var cleanup = factory.CreateDbContext();
        await cleanup.Database.EnsureDeletedAsync();
    }

    private sealed record TestFixture(TestDbContextFactory Factory, string ConnectionString);

    private sealed class TestDbContextFactory(DbContextOptions<THubDbContext> options)
        : IDbContextFactory<THubDbContext>
    {
        public THubDbContext CreateDbContext() => new(options);
    }

    private sealed class BlockingStepInsertInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<bool> _insertStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InsertStarted => _insertStarted.Task;

        public void Release() => _release.TrySetResult(true);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await WaitIfStepInsertAsync(command, cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await WaitIfStepInsertAsync(command, cancellationToken);
            return result;
        }

        private async Task WaitIfStepInsertAsync(
            DbCommand command,
            CancellationToken cancellationToken)
        {
            if (!command.CommandText.Contains(
                    "INSERT INTO [thub].[WorkflowStepRuns]",
                    StringComparison.Ordinal))
            {
                return;
            }

            _insertStarted.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken);
        }
    }
}
