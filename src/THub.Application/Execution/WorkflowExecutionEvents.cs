using THub.Domain.Runs;

namespace THub.Application.Execution;

public enum WorkflowExecutionEventKind
{
    ExecutionStarted,
    NodeStarted,
    NodeProgressed,
    NodeRetryScheduled,
    NodeSucceeded,
    NodeFailed,
    NodeCancelled,
    NodeSkipped,
    ExecutionSucceeded,
    ExecutionFailed,
    ExecutionCancelled
}

public sealed record WorkflowExecutionEvent(
    Guid WorkflowRunId,
    WorkflowExecutionEventKind Kind,
    DateTimeOffset OccurredAtUtc,
    string? NodeId = null,
    int? Attempt = null,
    WorkflowNodeProgress? Progress = null,
    ExecutionError? Error = null,
    TimeSpan? RetryDelay = null,
    string? ReasonCode = null);

public interface IWorkflowExecutionEventSink
{
    ValueTask WriteAsync(
        WorkflowExecutionEvent executionEvent,
        CancellationToken cancellationToken);
}

public sealed class WorkflowExecutionEventSinkException : Exception
{
    public WorkflowExecutionEventSinkException(Exception innerException)
        : base("The durable workflow execution event sink is unavailable.", innerException)
    {
    }
}

public sealed class NullWorkflowExecutionEventSink : IWorkflowExecutionEventSink
{
    public ValueTask WriteAsync(
        WorkflowExecutionEvent executionEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executionEvent);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

public enum WorkflowExecutionStatus
{
    Succeeded,
    Failed,
    Cancelled
}

public enum WorkflowNodeExecutionStatus
{
    Succeeded,
    Failed,
    Cancelled,
    Skipped
}

public sealed record WorkflowNodeExecutionOutcome(
    string NodeId,
    WorkflowNodeExecutionStatus Status,
    int Attempts,
    WorkflowNodeProgress Progress,
    ExecutionError? Error = null,
    string? ReasonCode = null);

public sealed record WorkflowExecutionResult(
    Guid WorkflowRunId,
    WorkflowExecutionStatus Status,
    IReadOnlyList<WorkflowNodeExecutionOutcome> NodeOutcomes,
    ExecutionError? Error = null);
