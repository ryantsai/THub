using System.Globalization;
using Quartz;
using THub.Application.Scheduling;

namespace THub.Worker.Scheduling;

internal static class QuartzWorkflowScheduleFactory
{
    public const string WorkflowIdKey = "WorkflowId";
    public const string WorkflowVersionKey = "WorkflowVersion";
    public const string CronExpressionKey = "CronExpression";
    public const string TimeZoneIdKey = "TimeZoneId";
    public const string ScheduledForUtcKey = "ScheduledForUtc";

    public static IJobDetail CreateJob(WorkflowSchedule schedule) =>
        JobBuilder.Create<EnqueueScheduledWorkflowJob>()
            .WithIdentity(QuartzWorkflowKeys.WorkflowJob(schedule.WorkflowId))
            .UsingJobData(WorkflowIdKey, schedule.WorkflowId.ToString("D"))
            .UsingJobData(
                WorkflowVersionKey,
                schedule.WorkflowVersion.ToString(CultureInfo.InvariantCulture))
            .UsingJobData(CronExpressionKey, schedule.CronExpression)
            .UsingJobData(TimeZoneIdKey, schedule.TimeZoneId)
            .RequestRecovery()
            .StoreDurably()
            .Build();

    public static ITrigger? CreateTrigger(
        WorkflowSchedule schedule,
        ScheduleCalculator scheduleCalculator,
        DateTimeOffset evaluatedAtUtc)
    {
        var nextRunAtUtc = schedule.NextRunAtUtc
            ?? scheduleCalculator.GetNextOccurrence(
                schedule.CronExpression,
                schedule.TimeZoneId,
                evaluatedAtUtc);

        if (nextRunAtUtc is null)
        {
            return null;
        }

        return TriggerBuilder.Create()
            .WithIdentity(QuartzWorkflowKeys.WorkflowTrigger(schedule.WorkflowId))
            .ForJob(QuartzWorkflowKeys.WorkflowJob(schedule.WorkflowId))
            .UsingJobData(
                ScheduledForUtcKey,
                nextRunAtUtc.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
            .StartAt(nextRunAtUtc.Value)
            .WithSimpleSchedule(scheduleBuilder =>
                scheduleBuilder.WithMisfireHandlingInstructionFireNow())
            .Build();
    }

    public static bool Matches(IJobDetail job, WorkflowSchedule schedule) =>
        string.Equals(
            job.JobDataMap.GetString(WorkflowIdKey),
            schedule.WorkflowId.ToString("D"),
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            job.JobDataMap.GetString(WorkflowVersionKey),
            schedule.WorkflowVersion.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal)
        && string.Equals(
            job.JobDataMap.GetString(CronExpressionKey),
            schedule.CronExpression,
            StringComparison.Ordinal)
        && string.Equals(
            job.JobDataMap.GetString(TimeZoneIdKey),
            schedule.TimeZoneId,
            StringComparison.Ordinal);

    public static bool Matches(ITrigger trigger, ITrigger desiredTrigger) =>
        trigger.StartTimeUtc == desiredTrigger.StartTimeUtc
        && string.Equals(
            trigger.JobDataMap.GetString(ScheduledForUtcKey),
            desiredTrigger.JobDataMap.GetString(ScheduledForUtcKey),
            StringComparison.Ordinal);
}
