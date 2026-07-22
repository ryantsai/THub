using System.Globalization;
using Quartz;
using THub.Worker.Scheduling;

namespace THub.Worker.Tests;

public sealed class EnqueueScheduledWorkflowJobTests
{
    [Fact]
    public void ReadsValidPersistedMetadata()
    {
        var workflowId = Guid.NewGuid();
        var occurrence = new DateTimeOffset(2026, 7, 23, 1, 2, 3, TimeSpan.Zero);
        var data = new JobDataMap
        {
            [QuartzWorkflowScheduleFactory.WorkflowIdKey] = workflowId.ToString("D"),
            [QuartzWorkflowScheduleFactory.WorkflowVersionKey] = "3",
            [QuartzWorkflowScheduleFactory.ScheduledForUtcKey] = occurrence.ToString(
                "O",
                CultureInfo.InvariantCulture)
        };

        Assert.Equal(workflowId, EnqueueScheduledWorkflowJob.ReadWorkflowId(data));
        Assert.Equal(3, EnqueueScheduledWorkflowJob.ReadWorkflowVersion(data));
        Assert.Equal(occurrence, EnqueueScheduledWorkflowJob.ReadScheduledOccurrence(data));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public void InvalidWorkflowIdUnschedulesCorruptTrigger(string value)
    {
        var exception = Assert.Throws<JobExecutionException>(() =>
            EnqueueScheduledWorkflowJob.ReadWorkflowId(new JobDataMap
            {
                [QuartzWorkflowScheduleFactory.WorkflowIdKey] = value
            }));

        Assert.True(exception.UnscheduleFiringTrigger);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("not-an-int")]
    public void InvalidWorkflowVersionUnschedulesCorruptTrigger(string value)
    {
        var exception = Assert.Throws<JobExecutionException>(() =>
            EnqueueScheduledWorkflowJob.ReadWorkflowVersion(new JobDataMap
            {
                [QuartzWorkflowScheduleFactory.WorkflowVersionKey] = value
            }));

        Assert.True(exception.UnscheduleFiringTrigger);
    }

    [Fact]
    public void NonRoundTripOccurrenceUnschedulesCorruptTrigger()
    {
        var exception = Assert.Throws<JobExecutionException>(() =>
            EnqueueScheduledWorkflowJob.ReadScheduledOccurrence(new JobDataMap
            {
                [QuartzWorkflowScheduleFactory.ScheduledForUtcKey] = "2026-07-23"
            }));

        Assert.True(exception.UnscheduleFiringTrigger);
    }
}
