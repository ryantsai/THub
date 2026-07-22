using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using THub.Application.Execution;
using THub.Domain.Runs;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Execution;

public sealed class SqlWorkflowExecutionEventSinkFactory(
    IDbContextFactory<THubDbContext> contextFactory) : IWorkflowExecutionEventSinkFactory
{
    private readonly IDbContextFactory<THubDbContext> _contextFactory =
        contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    public IWorkflowExecutionEventSink Create(Guid workflowRunId, string leaseOwner) =>
        new SqlWorkflowExecutionEventSink(
            _contextFactory,
            workflowRunId,
            leaseOwner);
}

internal sealed class SqlWorkflowExecutionEventSink : IWorkflowExecutionEventSink
{
    private readonly IDbContextFactory<THubDbContext> _contextFactory;
    private readonly Guid _workflowRunId;
    private readonly string _leaseOwner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(string NodeId, int Attempt), Guid> _attemptIds = new();

    public SqlWorkflowExecutionEventSink(
        IDbContextFactory<THubDbContext> contextFactory,
        Guid workflowRunId,
        string leaseOwner)
    {
        if (workflowRunId == Guid.Empty)
        {
            throw new ArgumentException("A workflow run id is required.", nameof(workflowRunId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        _contextFactory = contextFactory;
        _workflowRunId = workflowRunId;
        _leaseOwner = leaseOwner;
    }

    public async ValueTask WriteAsync(
        WorkflowExecutionEvent executionEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executionEvent);
        if (executionEvent.WorkflowRunId != _workflowRunId)
        {
            throw new InvalidOperationException("The event belongs to a different workflow run.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _ = await THubDbExecution.ExecuteAsync(
                _contextFactory,
                async operationToken =>
                {
                    await using var db = await _contextFactory.CreateDbContextAsync(operationToken);
                    await using var transaction = await db.Database.BeginTransactionAsync(
                        IsolationLevel.ReadCommitted,
                        operationToken);
                    await EnsureLeaseAsync(db, operationToken);

                    switch (executionEvent.Kind)
                    {
                        case WorkflowExecutionEventKind.ExecutionStarted:
                        case WorkflowExecutionEventKind.ExecutionSucceeded:
                        case WorkflowExecutionEventKind.ExecutionFailed:
                        case WorkflowExecutionEventKind.ExecutionCancelled:
                            break;
                        case WorkflowExecutionEventKind.NodeStarted:
                            await StartStepAsync(db, executionEvent, operationToken);
                            break;
                        case WorkflowExecutionEventKind.NodeProgressed:
                            await UpdateStepAsync(db, executionEvent, StepTransition.Progress, operationToken);
                            break;
                        case WorkflowExecutionEventKind.NodeRetryScheduled:
                        case WorkflowExecutionEventKind.NodeFailed:
                            await UpdateStepAsync(db, executionEvent, StepTransition.Fail, operationToken);
                            break;
                        case WorkflowExecutionEventKind.NodeSucceeded:
                            await UpdateStepAsync(db, executionEvent, StepTransition.Succeed, operationToken);
                            break;
                        case WorkflowExecutionEventKind.NodeCancelled:
                            await CancelStepAsync(db, executionEvent, operationToken);
                            break;
                        case WorkflowExecutionEventKind.NodeSkipped:
                            await SkipStepAsync(db, executionEvent, operationToken);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(executionEvent));
                    }

                    await db.SaveChangesAsync(operationToken);
                    await transaction.CommitAsync(operationToken);
                    return true;
                },
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLeaseAsync(THubDbContext db, CancellationToken cancellationToken)
    {
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("A lease guard requires an active database transaction.");
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandType = CommandType.Text;
        command.CommandTimeout = 30;
        command.CommandText =
            """
            SELECT TOP (1) 1
            FROM [thub].[WorkflowRuns] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Id] = @WorkflowRunId
                AND [Status] = N'Running'
                AND [LeaseOwner] = @LeaseOwner
                AND [LeaseExpiresAtUtc] > SYSUTCDATETIME();
            """;

        var runId = command.CreateParameter();
        runId.ParameterName = "@WorkflowRunId";
        runId.DbType = DbType.Guid;
        runId.Value = _workflowRunId;
        _ = command.Parameters.Add(runId);

        var leaseOwner = command.CreateParameter();
        leaseOwner.ParameterName = "@LeaseOwner";
        leaseOwner.DbType = DbType.String;
        leaseOwner.Size = WorkflowRun.MaximumLeaseOwnerLength;
        leaseOwner.Value = _leaseOwner;
        _ = command.Parameters.Add(leaseOwner);

        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new InvalidOperationException("The workflow run lease is no longer owned by this worker.");
        }
    }

    private async Task StartStepAsync(
        THubDbContext db,
        WorkflowExecutionEvent executionEvent,
        CancellationToken cancellationToken)
    {
        var nodeId = RequireNodeId(executionEvent);
        var engineAttempt = RequireAttempt(executionEvent);
        var existing = await db.WorkflowStepRuns
            .Where(step => step.WorkflowRunId == _workflowRunId && step.NodeId == nodeId)
            .OrderByDescending(step => step.Attempt)
            .ToListAsync(cancellationToken);

        foreach (var abandoned in existing.Where(step => step.Status == WorkflowStepRunStatus.Running))
        {
            abandoned.CompleteFailed(
                new ExecutionError(
                    "execution.lease-recovered",
                    ExecutionErrorCategory.Unknown,
                    "The previous worker stopped before completing this node attempt.",
                    isRetryable: false),
                executionEvent.OccurredAtUtc);
        }

        var durableAttempt = existing.Count == 0 ? 1 : checked(existing[0].Attempt + 1);
        var step = new WorkflowStepRun(
            _workflowRunId,
            nodeId,
            durableAttempt,
            executionEvent.OccurredAtUtc);
        step.Start(executionEvent.OccurredAtUtc);
        db.WorkflowStepRuns.Add(step);
        _attemptIds[(nodeId, engineAttempt)] = step.Id;
    }

    private async Task UpdateStepAsync(
        THubDbContext db,
        WorkflowExecutionEvent executionEvent,
        StepTransition transition,
        CancellationToken cancellationToken)
    {
        var step = await FindRunningStepAsync(db, executionEvent, cancellationToken);
        if (step is null)
        {
            throw new InvalidOperationException("No running durable step attempt matches the node event.");
        }

        RecordProgress(step, executionEvent.Progress);
        switch (transition)
        {
            case StepTransition.Progress:
                break;
            case StepTransition.Succeed:
                step.CompleteSucceeded(executionEvent.OccurredAtUtc);
                break;
            case StepTransition.Fail:
                step.CompleteFailed(
                    executionEvent.Error ?? new ExecutionError(
                        "execution.node.failed",
                        ExecutionErrorCategory.Unknown,
                        "The workflow node failed without a normalized error.",
                        isRetryable: false),
                    executionEvent.OccurredAtUtc);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(transition));
        }
    }

    private async Task CancelStepAsync(
        THubDbContext db,
        WorkflowExecutionEvent executionEvent,
        CancellationToken cancellationToken)
    {
        var step = await FindRunningStepAsync(db, executionEvent, cancellationToken);
        if (step is null)
        {
            var nodeId = RequireNodeId(executionEvent);
            var attempts = await db.WorkflowStepRuns
                .Where(candidate => candidate.WorkflowRunId == _workflowRunId
                    && candidate.NodeId == nodeId)
                .Select(candidate => candidate.Attempt)
                .ToListAsync(cancellationToken);
            step = new WorkflowStepRun(
                _workflowRunId,
                nodeId,
                attempts.Count == 0 ? 1 : checked(attempts.Max() + 1),
                executionEvent.OccurredAtUtc);
            step.Start(executionEvent.OccurredAtUtc);
            db.WorkflowStepRuns.Add(step);
        }

        RecordProgress(step, executionEvent.Progress);
        step.CompleteCancelled(executionEvent.OccurredAtUtc);
    }

    private async Task SkipStepAsync(
        THubDbContext db,
        WorkflowExecutionEvent executionEvent,
        CancellationToken cancellationToken)
    {
        var nodeId = RequireNodeId(executionEvent);
        var attempts = await db.WorkflowStepRuns
            .Where(candidate => candidate.WorkflowRunId == _workflowRunId
                && candidate.NodeId == nodeId)
            .Select(candidate => candidate.Attempt)
            .ToListAsync(cancellationToken);
        var step = new WorkflowStepRun(
            _workflowRunId,
            nodeId,
            attempts.Count == 0 ? 1 : checked(attempts.Max() + 1),
            executionEvent.OccurredAtUtc);
        step.Skip(executionEvent.OccurredAtUtc);
        db.WorkflowStepRuns.Add(step);
    }

    private async Task<WorkflowStepRun?> FindRunningStepAsync(
        THubDbContext db,
        WorkflowExecutionEvent executionEvent,
        CancellationToken cancellationToken)
    {
        var nodeId = RequireNodeId(executionEvent);
        if (executionEvent.Attempt is int attempt
            && _attemptIds.TryGetValue((nodeId, attempt), out var stepId))
        {
            return await db.WorkflowStepRuns.SingleOrDefaultAsync(
                step => step.Id == stepId && step.Status == WorkflowStepRunStatus.Running,
                cancellationToken);
        }

        return await db.WorkflowStepRuns
            .Where(step => step.WorkflowRunId == _workflowRunId
                && step.NodeId == nodeId
                && step.Status == WorkflowStepRunStatus.Running)
            .OrderByDescending(step => step.Attempt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static void RecordProgress(WorkflowStepRun step, WorkflowNodeProgress? total)
    {
        if (total is null)
        {
            return;
        }

        var rowsRead = checked(total.RowsRead - step.RowsRead);
        var rowsWritten = checked(total.RowsWritten - step.RowsWritten);
        var batches = checked(total.BatchesProcessed - step.BatchesProcessed);
        var bytesRead = checked(total.BytesRead - step.BytesRead);
        var bytesWritten = checked(total.BytesWritten - step.BytesWritten);
        if (rowsRead < 0 || rowsWritten < 0 || batches < 0 || bytesRead < 0 || bytesWritten < 0)
        {
            throw new InvalidOperationException("A node progress total cannot move backwards.");
        }

        if (rowsRead != 0 || rowsWritten != 0 || batches != 0 || bytesRead != 0 || bytesWritten != 0)
        {
            step.RecordProgress(rowsRead, rowsWritten, batches, bytesRead, bytesWritten);
        }
    }

    private static string RequireNodeId(WorkflowExecutionEvent executionEvent) =>
        string.IsNullOrWhiteSpace(executionEvent.NodeId)
            ? throw new InvalidOperationException("A node event requires a node id.")
            : executionEvent.NodeId;

    private static int RequireAttempt(WorkflowExecutionEvent executionEvent) =>
        executionEvent.Attempt is > 0
            ? executionEvent.Attempt.Value
            : throw new InvalidOperationException("A node-start event requires an attempt number.");

    private enum StepTransition
    {
        Progress,
        Succeed,
        Fail
    }
}

public sealed class SqlWorkflowStepRunLocator(
    IDbContextFactory<THubDbContext> contextFactory) : IWorkflowStepRunLocator
{
    private readonly IDbContextFactory<THubDbContext> _contextFactory =
        contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    public async Task<Guid?> FindRunningStepIdAsync(
        Guid workflowRunId,
        string nodeId,
        CancellationToken cancellationToken)
    {
        if (workflowRunId == Guid.Empty)
        {
            throw new ArgumentException("A workflow run id is required.", nameof(workflowRunId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.WorkflowStepRuns
            .AsNoTracking()
            .Where(step => step.WorkflowRunId == workflowRunId
                && step.NodeId == nodeId
                && step.Status == WorkflowStepRunStatus.Running)
            .OrderByDescending(step => step.Attempt)
            .Select(step => (Guid?)step.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
