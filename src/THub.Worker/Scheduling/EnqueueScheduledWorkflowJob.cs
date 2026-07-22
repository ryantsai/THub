using System.Globalization;
using Quartz;
using THub.Application.Scheduling;

namespace THub.Worker.Scheduling;

[DisallowConcurrentExecution]
public sealed class EnqueueScheduledWorkflowJob(
    IScheduledWorkflowRunEnqueuer enqueuer,
    ILogger<EnqueueScheduledWorkflowJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var workflowId = Guid.Parse(
            context.MergedJobDataMap.GetString(QuartzWorkflowScheduleFactory.WorkflowIdKey)
                ?? throw new InvalidOperationException("Quartz workflow ID is missing."));
        var workflowVersion = int.Parse(
            context.MergedJobDataMap.GetString(QuartzWorkflowScheduleFactory.WorkflowVersionKey)
                ?? throw new InvalidOperationException("Quartz workflow version is missing."),
            CultureInfo.InvariantCulture);
        var scheduledForUtc = DateTimeOffset.Parse(
            context.MergedJobDataMap.GetString(QuartzWorkflowScheduleFactory.ScheduledForUtcKey)
                ?? throw new InvalidOperationException("Quartz scheduled occurrence is missing."),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();

        var result = await enqueuer.EnqueueAsync(
            workflowId,
            workflowVersion,
            scheduledForUtc,
            DateTimeOffset.UtcNow,
            context.CancellationToken);

        switch (result.Status)
        {
            case ScheduledRunEnqueueStatus.Enqueued:
                logger.LogInformation(
                    "Queued workflow run {RunId} for workflow {WorkflowId} version {WorkflowVersion}, scheduled for {ScheduledForUtc}.",
                    result.RunId,
                    workflowId,
                    workflowVersion,
                    scheduledForUtc);
                break;
            case ScheduledRunEnqueueStatus.Duplicate:
                logger.LogInformation(
                    "Skipped duplicate scheduled occurrence for workflow {WorkflowId} version {WorkflowVersion} at {ScheduledForUtc}.",
                    workflowId,
                    workflowVersion,
                    scheduledForUtc);
                break;
            case ScheduledRunEnqueueStatus.NotEligible:
                logger.LogWarning(
                    "Skipped stale Quartz occurrence for workflow {WorkflowId} version {WorkflowVersion}; the workflow is no longer eligible.",
                    workflowId,
                    workflowVersion);
                break;
            default:
                throw new InvalidOperationException($"Unsupported enqueue status '{result.Status}'.");
        }
    }
}
