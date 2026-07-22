namespace THub.Application.Scheduling;

public interface ISchedulerCoordinator
{
    Task<int> EnqueueDueWorkflowsAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken);
}

