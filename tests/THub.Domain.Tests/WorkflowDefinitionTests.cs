using THub.Domain.Workflows;
using THub.Domain.Runs;

namespace THub.Domain.Tests;

public sealed class WorkflowDefinitionTests
{
    [Fact]
    public void NewWorkflowStartsAsVersionedDraft()
    {
        var workflow = new WorkflowDefinition("Customer sync", "DOMAIN\\user", "{\"nodes\":[]}");

        Assert.Equal(WorkflowStatus.Draft, workflow.Status);
        Assert.Equal(1, workflow.Version);
        Assert.Equal("Customer sync", workflow.Name);
    }

    [Fact]
    public void UpdatingGraphCreatesNewDraftVersion()
    {
        var workflow = new WorkflowDefinition("Customer sync", "DOMAIN\\user", "{}");
        workflow.Publish();

        workflow.UpdateGraph("{\"nodes\":[{}]}");

        Assert.Equal(2, workflow.Version);
        Assert.Equal(WorkflowStatus.Draft, workflow.Status);
    }

    [Fact]
    public void ScheduledRunRetainsItsLogicalOccurrence()
    {
        var scheduledForUtc = new DateTimeOffset(2026, 7, 22, 12, 30, 0, TimeSpan.Zero);

        var run = new WorkflowRun(Guid.NewGuid(), 3, "quartz", scheduledForUtc);

        Assert.Equal(scheduledForUtc, run.ScheduledForUtc);
        Assert.Equal("quartz", run.TriggeredBy);
        Assert.Equal(WorkflowRunStatus.Queued, run.Status);
    }
}
