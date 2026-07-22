namespace THub.Application.Scheduling;

public enum ScheduledRunEnqueueStatus
{
    Enqueued,
    Duplicate,
    NotEligible
}

public sealed record ScheduledRunEnqueueResult(
    ScheduledRunEnqueueStatus Status,
    Guid? RunId,
    DateTimeOffset? NextRunAtUtc);
