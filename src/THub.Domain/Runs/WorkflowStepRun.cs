namespace THub.Domain.Runs;

public enum WorkflowStepRunStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Skipped
}

/// <summary>
/// One durable attempt to execute one node in a workflow run.
/// </summary>
public sealed class WorkflowStepRun
{
    public const int MaximumNodeIdLength = 200;

    private WorkflowStepRun() { }

    public WorkflowStepRun(
        Guid workflowRunId,
        string nodeId,
        int attempt,
        DateTimeOffset queuedAtUtc)
    {
        Id = Guid.NewGuid();
        WorkflowRunId = DomainGuard.RequireId(workflowRunId, nameof(workflowRunId));
        NodeId = DomainGuard.Require(nodeId, nameof(nodeId), MaximumNodeIdLength);
        Attempt = DomainGuard.RequirePositive(attempt, nameof(attempt));
        QueuedAtUtc = DomainGuard.Utc(queuedAtUtc, nameof(queuedAtUtc));
    }

    public Guid Id { get; private set; }

    public Guid WorkflowRunId { get; private set; }

    public string NodeId { get; private set; } = string.Empty;

    public int Attempt { get; private set; }

    public WorkflowStepRunStatus Status { get; private set; } = WorkflowStepRunStatus.Queued;

    public DateTimeOffset QueuedAtUtc { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public long RowsRead { get; private set; }

    public long RowsWritten { get; private set; }

    public long BatchesProcessed { get; private set; }

    public long BytesRead { get; private set; }

    public long BytesWritten { get; private set; }

    public ExecutionError? Error { get; private set; }

    public bool IsTerminal =>
        Status is WorkflowStepRunStatus.Succeeded
            or WorkflowStepRunStatus.Failed
            or WorkflowStepRunStatus.Cancelled
            or WorkflowStepRunStatus.Skipped;

    public void Start(DateTimeOffset startedAtUtc)
    {
        EnsureStatus(WorkflowStepRunStatus.Queued);
        StartedAtUtc = DomainGuard.OnOrAfter(
            startedAtUtc,
            QueuedAtUtc,
            nameof(startedAtUtc));
        Status = WorkflowStepRunStatus.Running;
    }

    public void RecordProgress(
        long rowsRead,
        long rowsWritten,
        long batchesProcessed,
        long bytesRead = 0,
        long bytesWritten = 0)
    {
        EnsureStatus(WorkflowStepRunStatus.Running);
        RequireNonNegative(rowsRead, nameof(rowsRead));
        RequireNonNegative(rowsWritten, nameof(rowsWritten));
        RequireNonNegative(batchesProcessed, nameof(batchesProcessed));
        RequireNonNegative(bytesRead, nameof(bytesRead));
        RequireNonNegative(bytesWritten, nameof(bytesWritten));

        RowsRead = checked(RowsRead + rowsRead);
        RowsWritten = checked(RowsWritten + rowsWritten);
        BatchesProcessed = checked(BatchesProcessed + batchesProcessed);
        BytesRead = checked(BytesRead + bytesRead);
        BytesWritten = checked(BytesWritten + bytesWritten);
    }

    public void CompleteSucceeded(DateTimeOffset completedAtUtc) =>
        Complete(WorkflowStepRunStatus.Succeeded, completedAtUtc, error: null);

    public void CompleteFailed(ExecutionError error, DateTimeOffset completedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(error);
        Complete(WorkflowStepRunStatus.Failed, completedAtUtc, error);
    }

    public void CompleteCancelled(DateTimeOffset completedAtUtc) =>
        Complete(WorkflowStepRunStatus.Cancelled, completedAtUtc, error: null);

    public void Skip(DateTimeOffset skippedAtUtc)
    {
        EnsureStatus(WorkflowStepRunStatus.Queued);
        Status = WorkflowStepRunStatus.Skipped;
        CompletedAtUtc = DomainGuard.OnOrAfter(
            skippedAtUtc,
            QueuedAtUtc,
            nameof(skippedAtUtc));
    }

    private void Complete(
        WorkflowStepRunStatus status,
        DateTimeOffset completedAtUtc,
        ExecutionError? error)
    {
        EnsureStatus(WorkflowStepRunStatus.Running);
        var timestamp = DomainGuard.OnOrAfter(
            completedAtUtc,
            StartedAtUtc!.Value,
            nameof(completedAtUtc));

        Status = status;
        CompletedAtUtc = timestamp;
        Error = error;
    }

    private void EnsureStatus(WorkflowStepRunStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException(
                $"Step attempt must be {expected}, but it is {Status}.");
        }
    }

    private static void RequireNonNegative(long value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Progress counters cannot be negative.");
        }
    }
}
