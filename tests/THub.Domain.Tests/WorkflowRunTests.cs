using THub.Domain.Runs;
using THub.Domain.Workflows;

namespace THub.Domain.Tests;

public sealed class WorkflowRunTests
{
    private static readonly DateTimeOffset QueuedAt =
        new(2026, 7, 23, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ClaimHeartbeatAndExpiredLeaseRecoveryTrackAttempts()
    {
        var run = CreateRun();

        Assert.True(run.TryClaim("worker-a", QueuedAt, TimeSpan.FromMinutes(1)));
        Assert.False(run.TryClaim("worker-b", QueuedAt.AddSeconds(30), TimeSpan.FromMinutes(1)));
        run.RenewLease("worker-a", QueuedAt.AddSeconds(30), TimeSpan.FromMinutes(1));
        Assert.True(run.TryClaim("worker-b", QueuedAt.AddMinutes(2), TimeSpan.FromMinutes(1)));

        Assert.Equal(WorkflowRunStatus.Running, run.Status);
        Assert.Equal(2, run.AttemptCount);
        Assert.Equal("worker-b", run.LeaseOwner);
        Assert.Equal(QueuedAt, run.StartedAtUtc);
        Assert.Throws<InvalidOperationException>(() =>
            run.RenewLease("worker-a", QueuedAt.AddMinutes(2), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void OnlyActiveLeaseOwnerCanRecordTerminalFailure()
    {
        var run = CreateRun();
        run.TryClaim("worker-a", QueuedAt, TimeSpan.FromMinutes(1));
        var error = new ExecutionError(
            "sql.timeout",
            ExecutionErrorCategory.Timeout,
            "The command timed out.",
            isRetryable: true);

        Assert.Throws<InvalidOperationException>(() =>
            run.CompleteFailed("worker-b", error, QueuedAt.AddSeconds(10)));

        run.CompleteFailed("worker-a", error, QueuedAt.AddSeconds(10));

        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Same(error, run.Error);
        Assert.Equal(error.Summary, run.ErrorMessage);
        Assert.Null(run.LeaseOwner);
        Assert.False(run.TryClaim("worker-c", QueuedAt.AddMinutes(2), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void ExpiredLeaseCannotRecordSuccess()
    {
        var run = CreateRun();
        run.TryClaim("worker-a", QueuedAt, TimeSpan.FromSeconds(10));

        Assert.Throws<InvalidOperationException>(() =>
            run.CompleteSucceeded("worker-a", QueuedAt.AddSeconds(10)));
    }

    [Fact]
    public void CancellationIsImmediateWhenQueuedAndCooperativeWhenRunning()
    {
        var queued = CreateRun();
        Assert.True(queued.RequestCancellation("DOMAIN\\operator", QueuedAt.AddSeconds(1)));
        Assert.Equal(WorkflowRunStatus.Cancelled, queued.Status);
        Assert.Equal(QueuedAt.AddSeconds(1), queued.CompletedAtUtc);

        var running = CreateRun();
        running.TryClaim("worker-a", QueuedAt, TimeSpan.FromMinutes(1));
        Assert.True(running.RequestCancellation("DOMAIN\\operator", QueuedAt.AddSeconds(1)));
        Assert.Throws<InvalidOperationException>(() =>
            running.CompleteSucceeded("worker-a", QueuedAt.AddSeconds(2)));
        running.CompleteCancelled("worker-a", QueuedAt.AddSeconds(2));

        Assert.Equal(WorkflowRunStatus.Cancelled, running.Status);
        Assert.False(running.RequestCancellation("DOMAIN\\operator", QueuedAt.AddSeconds(3)));
    }

    [Fact]
    public void LegacySchedulerConstructorDerivesExactVersionIdentity()
    {
        var workflowId = Guid.NewGuid();
        var occurrence = QueuedAt.AddHours(1);

        var run = new WorkflowRun(workflowId, 3, "quartz", occurrence);

        Assert.Equal(WorkflowVersion.CreateId(workflowId, 3), run.WorkflowVersionId);
        Assert.Equal(occurrence, run.ScheduledForUtc);
    }

    [Fact]
    public void NormalizedErrorsRejectUnsafeOrOversizedValues()
    {
        Assert.Throws<ArgumentException>(() => new ExecutionError(
            "bad code",
            ExecutionErrorCategory.Unknown,
            "summary",
            false));
        Assert.Throws<ArgumentException>(() => new ExecutionError(
            "error.code",
            ExecutionErrorCategory.Unknown,
            "line one\nline two",
            false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExecutionError(
            "error.code",
            ExecutionErrorCategory.Unknown,
            new string('x', ExecutionError.MaximumSummaryLength + 1),
            false));
    }

    [Fact]
    public void StepAttemptsEnforceLifecycleAndAccumulateBoundedCounters()
    {
        var step = new WorkflowStepRun(Guid.NewGuid(), "source", 2, QueuedAt);

        Assert.Throws<InvalidOperationException>(() => step.RecordProgress(1, 0, 1));
        step.Start(QueuedAt.AddSeconds(1));
        step.RecordProgress(10, 4, 1, 100, 40);
        step.RecordProgress(5, 6, 1, 50, 60);
        step.CompleteSucceeded(QueuedAt.AddSeconds(2));

        Assert.Equal(15, step.RowsRead);
        Assert.Equal(10, step.RowsWritten);
        Assert.Equal(2, step.BatchesProcessed);
        Assert.Equal(WorkflowStepRunStatus.Succeeded, step.Status);
        Assert.Throws<InvalidOperationException>(() =>
            step.CompleteSucceeded(QueuedAt.AddSeconds(3)));
    }

    private static WorkflowRun CreateRun()
    {
        var previousRun = Guid.NewGuid();
        return new WorkflowRun(
            Guid.NewGuid(),
            Guid.NewGuid(),
            2,
            "DOMAIN\\operator",
            QueuedAt,
            retryOfRunId: previousRun);
    }
}
