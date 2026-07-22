using THub.Domain.Workflows;

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
}

