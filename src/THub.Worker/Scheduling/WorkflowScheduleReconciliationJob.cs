using Quartz;
using Quartz.Impl.Matchers;
using THub.Application.Scheduling;

namespace THub.Worker.Scheduling;

[DisallowConcurrentExecution]
public sealed class WorkflowScheduleReconciliationJob(
    IWorkflowScheduleSource scheduleSource,
    ScheduleCalculator scheduleCalculator,
    ILogger<WorkflowScheduleReconciliationJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var cancellationToken = context.CancellationToken;
        var schedules = await scheduleSource.GetPublishedSchedulesAsync(cancellationToken);
        var desiredJobKeys = new HashSet<JobKey>();

        foreach (var schedule in schedules)
        {
            var jobKey = QuartzWorkflowKeys.WorkflowJob(schedule.WorkflowId);
            desiredJobKeys.Add(jobKey);

            try
            {
                await ReconcileScheduleAsync(
                    context.Scheduler,
                    schedule,
                    DateTimeOffset.UtcNow,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(
                    exception,
                    "Could not reconcile the Quartz schedule for workflow {WorkflowId} version {WorkflowVersion}.",
                    schedule.WorkflowId,
                    schedule.WorkflowVersion);
                await context.Scheduler.DeleteJob(jobKey, cancellationToken);
            }
        }

        var existingJobKeys = await context.Scheduler.GetJobKeys(
            GroupMatcher<JobKey>.GroupEquals(QuartzWorkflowKeys.WorkflowGroup),
            cancellationToken);

        foreach (var staleJobKey in existingJobKeys.Except(desiredJobKeys))
        {
            await context.Scheduler.DeleteJob(staleJobKey, cancellationToken);
            logger.LogInformation("Removed stale Quartz workflow schedule {QuartzJobKey}.", staleJobKey);
        }
    }

    private async Task ReconcileScheduleAsync(
        IScheduler scheduler,
        WorkflowSchedule schedule,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken)
    {
        var jobKey = QuartzWorkflowKeys.WorkflowJob(schedule.WorkflowId);
        var triggerKey = QuartzWorkflowKeys.WorkflowTrigger(schedule.WorkflowId);
        var desiredJob = QuartzWorkflowScheduleFactory.CreateJob(schedule);
        var desiredTrigger = QuartzWorkflowScheduleFactory.CreateTrigger(
            schedule,
            scheduleCalculator,
            evaluatedAtUtc);

        if (desiredTrigger is null)
        {
            await scheduler.DeleteJob(jobKey, cancellationToken);
            return;
        }

        var existingJob = await scheduler.GetJobDetail(jobKey, cancellationToken);
        if (existingJob is null)
        {
            await scheduler.ScheduleJob(desiredJob, desiredTrigger, cancellationToken);
            logger.LogInformation(
                "Created Quartz schedule for workflow {WorkflowId} version {WorkflowVersion}; next fire is {NextFireAtUtc}.",
                schedule.WorkflowId,
                schedule.WorkflowVersion,
                desiredTrigger.StartTimeUtc);
            return;
        }

        if (!QuartzWorkflowScheduleFactory.Matches(existingJob, schedule))
        {
            await scheduler.AddJob(
                desiredJob,
                replace: true,
                storeNonDurableWhileAwaitingScheduling: true,
                cancellationToken);
        }

        var existingTrigger = await scheduler.GetTrigger(triggerKey, cancellationToken);
        if (existingTrigger is null)
        {
            await scheduler.ScheduleJob(desiredTrigger, cancellationToken);
            return;
        }

        if (!QuartzWorkflowScheduleFactory.Matches(existingTrigger, desiredTrigger))
        {
            await scheduler.RescheduleJob(triggerKey, desiredTrigger, cancellationToken);
        }
    }
}
