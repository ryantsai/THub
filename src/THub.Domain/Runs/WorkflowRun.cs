namespace THub.Domain.Runs;

public enum WorkflowRunStatus { Queued, Running, Succeeded, Failed, Cancelled }

public sealed class WorkflowRun
{
    private WorkflowRun() { }

    public WorkflowRun(Guid workflowId, int workflowVersion, string triggeredBy)
    {
        Id = Guid.NewGuid();
        WorkflowId = workflowId;
        WorkflowVersion = workflowVersion;
        TriggeredBy = triggeredBy;
        QueuedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid WorkflowId { get; private set; }
    public int WorkflowVersion { get; private set; }
    public WorkflowRunStatus Status { get; private set; } = WorkflowRunStatus.Queued;
    public string TriggeredBy { get; private set; } = string.Empty;
    public DateTimeOffset QueuedAtUtc { get; private set; }
    public DateTimeOffset? StartedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public string? ErrorMessage { get; private set; }
}

