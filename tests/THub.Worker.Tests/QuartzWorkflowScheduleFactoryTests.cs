using Quartz;
using THub.Application.Scheduling;
using THub.Worker.Scheduling;

namespace THub.Worker.Tests;

public sealed class QuartzWorkflowScheduleFactoryTests
{
    [Fact]
    public void CreatesStableJobIdentityAndMetadata()
    {
        var schedule = CreateSchedule(nextRunAtUtc: null);

        var job = QuartzWorkflowScheduleFactory.CreateJob(schedule);

        Assert.Equal(QuartzWorkflowKeys.WorkflowJob(schedule.WorkflowId), job.Key);
        Assert.Equal(schedule.WorkflowId.ToString("D"), job.JobDataMap.GetString("WorkflowId"));
        Assert.Equal("7", job.JobDataMap.GetString("WorkflowVersion"));
        Assert.True(QuartzWorkflowScheduleFactory.Matches(job, schedule));
        Assert.False(QuartzWorkflowScheduleFactory.Matches(job, schedule with { WorkflowVersion = 8 }));
    }

    [Fact]
    public void UsesPersistedNextOccurrenceForOneShotTrigger()
    {
        var nextRunAtUtc = new DateTimeOffset(2026, 7, 22, 4, 30, 0, TimeSpan.Zero);
        var schedule = CreateSchedule(nextRunAtUtc);

        var trigger = QuartzWorkflowScheduleFactory.CreateTrigger(
            schedule,
            new ScheduleCalculator(),
            nextRunAtUtc.AddHours(-1));

        var simpleTrigger = Assert.IsAssignableFrom<ISimpleTrigger>(trigger);
        Assert.Equal(QuartzWorkflowKeys.WorkflowTrigger(schedule.WorkflowId), simpleTrigger.Key);
        Assert.Equal(nextRunAtUtc, simpleTrigger.StartTimeUtc);
        Assert.Equal(QuartzWorkflowKeys.WorkflowJob(schedule.WorkflowId), simpleTrigger.JobKey);
        Assert.Equal(0, simpleTrigger.RepeatCount);
        Assert.Equal(
            nextRunAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            simpleTrigger.JobDataMap.GetString(QuartzWorkflowScheduleFactory.ScheduledForUtcKey));
        Assert.True(QuartzWorkflowScheduleFactory.Matches(simpleTrigger, simpleTrigger));
    }

    [Fact]
    public void CalculatesMissingNextOccurrenceUsingFiveFieldCron()
    {
        var evaluatedAtUtc = new DateTimeOffset(2026, 7, 22, 2, 7, 0, TimeSpan.Zero);
        var schedule = CreateSchedule(nextRunAtUtc: null);

        var trigger = QuartzWorkflowScheduleFactory.CreateTrigger(
            schedule,
            new ScheduleCalculator(),
            evaluatedAtUtc);

        var simpleTrigger = Assert.IsAssignableFrom<ISimpleTrigger>(trigger);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 22, 2, 15, 0, TimeSpan.Zero),
            simpleTrigger.StartTimeUtc);
    }

    private static WorkflowSchedule CreateSchedule(DateTimeOffset? nextRunAtUtc) =>
        new(
            Guid.Parse("18b733cb-cbb6-47ef-b619-9a2cf9b44934"),
            7,
            "*/15 * * * *",
            TimeZoneInfo.Utc.Id,
            nextRunAtUtc);
}
