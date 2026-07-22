namespace THub.Application.Scheduling;

public interface IScheduledWorkflowRunEnqueuer
{
    Task<ScheduledRunEnqueueResult> EnqueueAsync(
        Guid workflowId,
        int expectedWorkflowVersion,
        DateTimeOffset scheduledForUtc,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken);
}
