using THub.Domain.Workflows;

namespace THub.Domain.Tests;

public sealed class WorkflowDefinitionRevisionTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 7, 23, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CompletedMetadataSaveAdvancesRevision()
    {
        var workflow = CreateWorkflow();

        workflow.Rename("Renamed", CreatedAt.AddMinutes(1));
        workflow.CompleteSave(1, CreatedAt.AddMinutes(1));

        Assert.Equal(2, workflow.DraftRevision);
    }

    [Fact]
    public void CompletedGraphSaveDoesNotAdvanceRevisionTwice()
    {
        var workflow = CreateWorkflow();

        workflow.UpdateGraph("{\"schemaVersion\":1}", CreatedAt.AddMinutes(1));
        workflow.CompleteSave(1, CreatedAt.AddMinutes(1));

        Assert.Equal(2, workflow.DraftRevision);
    }

    [Fact]
    public void RejectsSaveThatWouldCoverMultipleEditRevisions()
    {
        var workflow = CreateWorkflow();
        workflow.UpdateGraph("{\"schemaVersion\":1}", CreatedAt.AddMinutes(1));
        workflow.UpdateGraph("{\"schemaVersion\":2}", CreatedAt.AddMinutes(2));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            workflow.CompleteSave(1, CreatedAt.AddMinutes(2)));

        Assert.Contains("exactly once", exception.Message, StringComparison.Ordinal);
    }

    private static WorkflowDefinition CreateWorkflow() => new(
        "Orders",
        "CONTOSO\\owner",
        "{\"schemaVersion\":0}",
        CreatedAt);
}
