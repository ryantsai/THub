namespace THub.Application.Scheduling;

public interface IWorkflowScheduleSource
{
    Task<IReadOnlyList<WorkflowSchedule>> GetPublishedSchedulesAsync(
        CancellationToken cancellationToken);
}
