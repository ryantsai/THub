using THub.Application.Alerts;
using THub.Application.Scheduling;
using THub.Application.Workflows;
using THub.Application.Workflows.Management;
using THub.Domain.Runs;

namespace THub.Application.Tests;

public sealed class WorkflowRunServiceTests
{
    private readonly FakeWorkflowManagementRepository repository = new();
    private readonly WorkflowManagementTimeProvider timeProvider =
        new(WorkflowManagementTestData.Now);
    private readonly TerminalAlertStore terminalAlertStore = new();

    [Fact]
    public async Task QueuesExactValidatedImmutableVersion()
    {
        var published = WorkflowManagementTestData.CreatePublished();
        repository.Workflow = published.Workflow;
        repository.Version = published.Version;

        var result = await CreateService().QueueAsync(new(
            published.Workflow.Id,
            "CONTOSO\\operator"));

        Assert.True(result.IsSuccess);
        Assert.Equal(published.Version.Id, repository.QueuedRun!.WorkflowVersionId);
        Assert.Equal(published.Version.Version, repository.QueuedRun.WorkflowVersion);
        Assert.Equal(published.Version.Id, repository.ReceivedExpectedVersionId);
        Assert.Equal(WorkflowManagementTestData.Now, repository.QueuedRun.QueuedAtUtc);
        Assert.Equal(1, repository.QueueCalls);
    }

    [Fact]
    public async Task QueueRejectsDraftWorkflow()
    {
        repository.Workflow = WorkflowManagementTestData.CreateDraft();

        var result = await CreateService().QueueAsync(new(
            repository.Workflow.Id,
            "CONTOSO\\operator"));

        Assert.Equal(WorkflowOperationStatus.InvalidState, result.Status);
        Assert.Equal("workflow.not-published", Assert.Single(result.Issues).Code);
        Assert.Equal(0, repository.QueueCalls);
    }

    [Fact]
    public async Task QueueRejectsMissingImmutableVersion()
    {
        var published = WorkflowManagementTestData.CreatePublished();
        repository.Workflow = published.Workflow;

        var result = await CreateService().QueueAsync(new(
            published.Workflow.Id,
            "CONTOSO\\operator"));

        Assert.Equal(WorkflowOperationStatus.InvalidState, result.Status);
        Assert.Equal("workflow.version.missing", Assert.Single(result.Issues).Code);
        Assert.Equal(0, repository.QueueCalls);
    }

    [Fact]
    public async Task QueueMapsActiveRunConflict()
    {
        var published = WorkflowManagementTestData.CreatePublished();
        repository.Workflow = published.Workflow;
        repository.Version = published.Version;
        repository.QueueResult = WorkflowStoreWriteResult.Conflict(
            "run.active-exists",
            "This workflow already has an active run.");

        var result = await CreateService().QueueAsync(new(
            published.Workflow.Id,
            "CONTOSO\\operator"));

        Assert.Equal(WorkflowOperationStatus.Conflict, result.Status);
        Assert.Equal("run.active-exists", Assert.Single(result.Issues).Code);
    }

    [Fact]
    public async Task CancelQueuedRunPersistsImmediateTerminalCancellation()
    {
        var published = WorkflowManagementTestData.CreatePublished();
        repository.Run = new WorkflowRun(
            published.Workflow.Id,
            published.Version.Id,
            published.Version.Version,
            "CONTOSO\\operator",
            WorkflowManagementTestData.Now.AddMinutes(-1));

        var result = await CreateService().CancelAsync(new(
            repository.Run.Id,
            "CONTOSO\\canceller"));

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkflowRunStatus.Cancelled, result.Value!.Status);
        Assert.Equal(WorkflowRunStatus.Queued, terminalAlertStore.ExpectedPreviousStatus);
        Assert.Equal("CONTOSO\\canceller", terminalAlertStore.TerminalRun!.CancellationRequestedBy);
        Assert.Equal(1, terminalAlertStore.CommitCalls);
        Assert.Equal(0, repository.CancellationCalls);
    }

    [Fact]
    public async Task CancelIsIdempotentAfterDurableRequest()
    {
        var published = WorkflowManagementTestData.CreatePublished();
        var run = new WorkflowRun(
            published.Workflow.Id,
            published.Version.Id,
            published.Version.Version,
            "CONTOSO\\operator",
            WorkflowManagementTestData.Now.AddMinutes(-5));
        Assert.True(run.TryClaim(
            "worker-1",
            WorkflowManagementTestData.Now.AddMinutes(-4),
            TimeSpan.FromMinutes(10)));
        Assert.True(run.RequestCancellation(
            "CONTOSO\\first-canceller",
            WorkflowManagementTestData.Now.AddMinutes(-3)));
        repository.Run = run;

        var result = await CreateService().CancelAsync(new(
            run.Id,
            "CONTOSO\\second-canceller"));

        Assert.True(result.IsSuccess);
        Assert.Equal("CONTOSO\\first-canceller", result.Value!.CancellationRequestedBy);
        Assert.Equal(0, repository.CancellationCalls);
    }

    [Fact]
    public async Task CancelRejectsTerminalRunWithoutCancellationRequest()
    {
        var published = WorkflowManagementTestData.CreatePublished();
        var run = new WorkflowRun(
            published.Workflow.Id,
            published.Version.Id,
            published.Version.Version,
            "CONTOSO\\operator",
            WorkflowManagementTestData.Now.AddMinutes(-5));
        Assert.True(run.TryClaim(
            "worker-1",
            WorkflowManagementTestData.Now.AddMinutes(-4),
            TimeSpan.FromMinutes(10)));
        run.CompleteSucceeded("worker-1", WorkflowManagementTestData.Now.AddMinutes(-3));
        repository.Run = run;

        var result = await CreateService().CancelAsync(new(
            run.Id,
            "CONTOSO\\canceller"));

        Assert.Equal(WorkflowOperationStatus.Conflict, result.Status);
        Assert.Equal("run.terminal", Assert.Single(result.Issues).Code);
        Assert.Equal(0, repository.CancellationCalls);
    }

    [Fact]
    public async Task CancelReturnsStructuredNotFound()
    {
        var result = await CreateService().CancelAsync(new(
            Guid.NewGuid(),
            "CONTOSO\\canceller"));

        Assert.Equal(WorkflowOperationStatus.NotFound, result.Status);
        Assert.Equal("run.not-found", Assert.Single(result.Issues).Code);
    }

    [Fact]
    public async Task CancelMapsOptimisticConcurrencyConflict()
    {
        var published = WorkflowManagementTestData.CreatePublished();
        var run = new WorkflowRun(
            published.Workflow.Id,
            published.Version.Id,
            published.Version.Version,
            "CONTOSO\\operator",
            WorkflowManagementTestData.Now.AddMinutes(-1));
        Assert.True(run.TryClaim(
            "worker-1",
            WorkflowManagementTestData.Now.AddSeconds(-30),
            TimeSpan.FromMinutes(5)));
        repository.Run = run;
        repository.CancellationResult = WorkflowStoreWriteResult.Concurrency();

        var result = await CreateService().CancelAsync(new(
            repository.Run.Id,
            "CONTOSO\\canceller"));

        Assert.Equal(WorkflowOperationStatus.ConcurrencyConflict, result.Status);
        Assert.Equal("workflow.concurrency", Assert.Single(result.Issues).Code);
    }

    private WorkflowRunService CreateService() => new(
        repository,
        new WorkflowGraphSerializer(),
        new WorkflowGraphValidator(),
        new ScheduleCalculator(),
        new WorkflowTerminalAlertService(terminalAlertStore, timeProvider),
        timeProvider);

    private sealed class TerminalAlertStore : IWorkflowTerminalAlertStore
    {
        public int CommitCalls { get; private set; }

        public WorkflowRun? TerminalRun { get; private set; }

        public WorkflowRunStatus? ExpectedPreviousStatus { get; private set; }

        public TerminalAlertCommitStatus CommitStatus { get; set; } =
            TerminalAlertCommitStatus.Saved;

        public Task<TerminalAlertPreparation?> LoadPreparationAsync(
            Guid workflowId,
            WorkflowRunStatus terminalStatus,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<TerminalAlertPreparation?>(new(
                "Test workflow",
                []));
        }

        public Task<TerminalAlertCommitStatus> CommitTerminalRunAsync(
            WorkflowRun terminalRun,
            WorkflowRunStatus expectedPreviousStatus,
            string? expectedLeaseOwner,
            IReadOnlyList<PreparedTerminalAlert> alerts,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommitCalls++;
            TerminalRun = terminalRun;
            ExpectedPreviousStatus = expectedPreviousStatus;
            return Task.FromResult(CommitStatus);
        }
    }
}
