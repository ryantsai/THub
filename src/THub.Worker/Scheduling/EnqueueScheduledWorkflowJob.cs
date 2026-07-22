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
        var workflowId = ReadWorkflowId(context.MergedJobDataMap);
        var workflowVersion = ReadWorkflowVersion(context.MergedJobDataMap);
        var scheduledForUtc = ReadScheduledOccurrence(context.MergedJobDataMap);

        ScheduledRunEnqueueResult result;
        try
        {
            result = await enqueuer.EnqueueAsync(
                workflowId,
                workflowVersion,
                scheduledForUtc,
                DateTimeOffset.UtcNow,
                context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Could not enqueue workflow {WorkflowId} version {WorkflowVersion} for scheduled occurrence {ScheduledForUtc}.",
                workflowId,
                workflowVersion,
                scheduledForUtc);
            throw new JobExecutionException(exception, refireImmediately: false);
        }

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

    internal static Guid ReadWorkflowId(JobDataMap data)
    {
        var value = data.GetString(QuartzWorkflowScheduleFactory.WorkflowIdKey);
        if (!Guid.TryParse(value, out var workflowId))
        {
            throw InvalidMetadata("workflow ID");
        }

        return workflowId;
    }

    internal static int ReadWorkflowVersion(JobDataMap data)
    {
        var value = data.GetString(QuartzWorkflowScheduleFactory.WorkflowVersionKey);
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || version < 1)
        {
            throw InvalidMetadata("workflow version");
        }

        return version;
    }

    internal static DateTimeOffset ReadScheduledOccurrence(JobDataMap data)
    {
        var value = data.GetString(QuartzWorkflowScheduleFactory.ScheduledForUtcKey);
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var occurrence))
        {
            throw InvalidMetadata("scheduled occurrence");
        }

        return occurrence.ToUniversalTime();
    }

    private static JobExecutionException InvalidMetadata(string field) =>
        new($"Quartz {field} is missing or malformed.")
        {
            UnscheduleFiringTrigger = true
        };
}
