using System.Collections.Concurrent;
using THub.Application.Execution;

namespace THub.Worker.Execution;

/// <summary>
/// Emits the safe structured trace for every workflow operation lifecycle event after the
/// authoritative event sink has persisted that event.
/// </summary>
internal sealed class WorkflowOperationTraceSink(
    IWorkflowExecutionEventSink inner,
    ILogger<WorkflowOperationTraceSink> logger,
    Guid workflowId,
    Guid workflowVersionId,
    int workflowVersion) : IWorkflowExecutionEventSink
{
    private readonly ConcurrentDictionary<(string NodeId, int Attempt), DateTimeOffset>
        _operationStarts = new();
    private DateTimeOffset? _executionStartedAtUtc;

    public async ValueTask WriteAsync(
        WorkflowExecutionEvent executionEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executionEvent);
        await inner.WriteAsync(executionEvent, cancellationToken).ConfigureAwait(false);

        try
        {
            WriteTrace(executionEvent);
        }
        catch (Exception exception) when (IsRecoverableTraceFailure(exception))
        {
            // Observability must not turn an already-persisted execution transition into a failure.
        }
    }

    private void WriteTrace(WorkflowExecutionEvent executionEvent)
    {
        switch (executionEvent.Kind)
        {
            case WorkflowExecutionEventKind.ExecutionStarted:
                _executionStartedAtUtc = executionEvent.OccurredAtUtc;
                logger.LogInformation(
                    "Workflow execution started for run {WorkflowRunId}, workflow {WorkflowId}, version {WorkflowVersion}, version id {WorkflowVersionId}.",
                    executionEvent.WorkflowRunId,
                    workflowId,
                    workflowVersion,
                    workflowVersionId);
                break;
            case WorkflowExecutionEventKind.ExecutionSucceeded:
                LogExecutionCompleted(executionEvent, "Succeeded", LogLevel.Information);
                break;
            case WorkflowExecutionEventKind.ExecutionFailed:
                LogExecutionCompleted(executionEvent, "Failed", LogLevel.Warning);
                break;
            case WorkflowExecutionEventKind.ExecutionCancelled:
                LogExecutionCompleted(executionEvent, "Cancelled", LogLevel.Information);
                break;
            case WorkflowExecutionEventKind.NodeStarted:
                LogOperationStarted(executionEvent);
                break;
            case WorkflowExecutionEventKind.NodeProgressed:
                LogOperationProgress(executionEvent);
                break;
            case WorkflowExecutionEventKind.NodeRetryScheduled:
                LogOperationRetry(executionEvent);
                break;
            case WorkflowExecutionEventKind.NodeSucceeded:
                LogOperationCompleted(executionEvent, "Succeeded", LogLevel.Information);
                break;
            case WorkflowExecutionEventKind.NodeFailed:
                LogOperationCompleted(executionEvent, "Failed", LogLevel.Warning);
                break;
            case WorkflowExecutionEventKind.NodeCancelled:
                LogOperationCompleted(executionEvent, "Cancelled", LogLevel.Information);
                break;
            case WorkflowExecutionEventKind.NodeSkipped:
                logger.LogInformation(
                    "Workflow operation {OperationName} skipped for run {WorkflowRunId}, workflow {WorkflowId}, version {WorkflowVersion}, version id {WorkflowVersionId}, node {NodeId}; reason {ReasonCode}.",
                    OperationName(executionEvent),
                    executionEvent.WorkflowRunId,
                    workflowId,
                    workflowVersion,
                    workflowVersionId,
                    executionEvent.NodeId,
                    executionEvent.ReasonCode);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(executionEvent));
        }
    }

    private void LogExecutionCompleted(
        WorkflowExecutionEvent executionEvent,
        string status,
        LogLevel level)
    {
        var durationMilliseconds = ElapsedMilliseconds(
            _executionStartedAtUtc,
            executionEvent.OccurredAtUtc);
        logger.Log(
            level,
            "Workflow execution completed with status {OperationStatus} for run {WorkflowRunId}, workflow {WorkflowId}, version {WorkflowVersion}, version id {WorkflowVersionId}, duration {DurationMilliseconds} ms; error code {ErrorCode}, category {ErrorCategory}, retryable {IsRetryable}.",
            status,
            executionEvent.WorkflowRunId,
            workflowId,
            workflowVersion,
            workflowVersionId,
            durationMilliseconds,
            executionEvent.Error?.Code,
            executionEvent.Error?.Category,
            executionEvent.Error?.IsRetryable);
    }

    private void LogOperationStarted(WorkflowExecutionEvent executionEvent)
    {
        if (executionEvent.NodeId is not null && executionEvent.Attempt is int attempt)
        {
            _operationStarts[(executionEvent.NodeId, attempt)] = executionEvent.OccurredAtUtc;
        }

        logger.LogInformation(
            "Workflow operation {OperationName} started for run {WorkflowRunId}, workflow {WorkflowId}, version {WorkflowVersion}, version id {WorkflowVersionId}, node {NodeId}, attempt {Attempt}.",
            OperationName(executionEvent),
            executionEvent.WorkflowRunId,
            workflowId,
            workflowVersion,
            workflowVersionId,
            executionEvent.NodeId,
            executionEvent.Attempt);
    }

    private void LogOperationProgress(WorkflowExecutionEvent executionEvent)
    {
        var progress = executionEvent.Progress ?? new WorkflowNodeProgress();
        logger.LogDebug(
            "Workflow operation {OperationName} progressed for run {WorkflowRunId}, workflow {WorkflowId}, version {WorkflowVersion}, version id {WorkflowVersionId}, node {NodeId}, attempt {Attempt}: rows read {RowsRead}, rows written {RowsWritten}, batches {BatchesProcessed}, bytes read {BytesRead}, bytes written {BytesWritten}.",
            OperationName(executionEvent),
            executionEvent.WorkflowRunId,
            workflowId,
            workflowVersion,
            workflowVersionId,
            executionEvent.NodeId,
            executionEvent.Attempt,
            progress.RowsRead,
            progress.RowsWritten,
            progress.BatchesProcessed,
            progress.BytesRead,
            progress.BytesWritten);
    }

    private void LogOperationRetry(WorkflowExecutionEvent executionEvent)
    {
        var durationMilliseconds = TakeOperationDuration(executionEvent);
        var progress = executionEvent.Progress ?? new WorkflowNodeProgress();
        logger.LogWarning(
            "Workflow operation {OperationName} scheduled a retry for run {WorkflowRunId}, workflow {WorkflowId}, version {WorkflowVersion}, version id {WorkflowVersionId}, node {NodeId}, attempt {Attempt}, duration {DurationMilliseconds} ms, delay {RetryDelayMilliseconds} ms; error code {ErrorCode}, category {ErrorCategory}, retryable {IsRetryable}; rows read {RowsRead}, rows written {RowsWritten}, batches {BatchesProcessed}, bytes read {BytesRead}, bytes written {BytesWritten}.",
            OperationName(executionEvent),
            executionEvent.WorkflowRunId,
            workflowId,
            workflowVersion,
            workflowVersionId,
            executionEvent.NodeId,
            executionEvent.Attempt,
            durationMilliseconds,
            executionEvent.RetryDelay?.TotalMilliseconds,
            executionEvent.Error?.Code,
            executionEvent.Error?.Category,
            executionEvent.Error?.IsRetryable,
            progress.RowsRead,
            progress.RowsWritten,
            progress.BatchesProcessed,
            progress.BytesRead,
            progress.BytesWritten);
    }

    private void LogOperationCompleted(
        WorkflowExecutionEvent executionEvent,
        string status,
        LogLevel level)
    {
        var durationMilliseconds = TakeOperationDuration(executionEvent);
        var progress = executionEvent.Progress ?? new WorkflowNodeProgress();
        logger.Log(
            level,
            "Workflow operation {OperationName} completed with status {OperationStatus} for run {WorkflowRunId}, workflow {WorkflowId}, version {WorkflowVersion}, version id {WorkflowVersionId}, node {NodeId}, attempt {Attempt}, duration {DurationMilliseconds} ms; error code {ErrorCode}, category {ErrorCategory}, retryable {IsRetryable}; rows read {RowsRead}, rows written {RowsWritten}, batches {BatchesProcessed}, bytes read {BytesRead}, bytes written {BytesWritten}.",
            OperationName(executionEvent),
            status,
            executionEvent.WorkflowRunId,
            workflowId,
            workflowVersion,
            workflowVersionId,
            executionEvent.NodeId,
            executionEvent.Attempt,
            durationMilliseconds,
            executionEvent.Error?.Code,
            executionEvent.Error?.Category,
            executionEvent.Error?.IsRetryable,
            progress.RowsRead,
            progress.RowsWritten,
            progress.BatchesProcessed,
            progress.BytesRead,
            progress.BytesWritten);
    }

    private double? TakeOperationDuration(WorkflowExecutionEvent executionEvent)
    {
        if (executionEvent.NodeId is null
            || executionEvent.Attempt is not int attempt
            || !_operationStarts.TryRemove(
                (executionEvent.NodeId, attempt),
                out var startedAtUtc))
        {
            return null;
        }

        return ElapsedMilliseconds(startedAtUtc, executionEvent.OccurredAtUtc);
    }

    private static double? ElapsedMilliseconds(
        DateTimeOffset? startedAtUtc,
        DateTimeOffset completedAtUtc) =>
        startedAtUtc is null
            ? null
            : Math.Max(0, (completedAtUtc - startedAtUtc.Value).TotalMilliseconds);

    private static string OperationName(WorkflowExecutionEvent executionEvent) =>
        executionEvent.NodeKind?.ToString() ?? "Unknown";

    private static bool IsRecoverableTraceFailure(Exception exception) =>
        exception is not OutOfMemoryException;
}
