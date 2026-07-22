using System.Reflection;
using THub.Domain.Workflows;

namespace THub.Domain.Tests;

public sealed class WorkflowDefinitionTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 7, 23, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NewWorkflowStartsAsVersionedDraftWithCallerTimestamp()
    {
        var workflow = new WorkflowDefinition(
            " Customer sync ",
            " DOMAIN\\user ",
            "{\"nodes\":[]}",
            CreatedAt.ToOffset(TimeSpan.FromHours(8)),
            " Synchronizes customers ");

        Assert.Equal(WorkflowStatus.Draft, workflow.Status);
        Assert.Equal(1, workflow.Version);
        Assert.Equal(1, workflow.DraftRevision);
        Assert.Equal("Customer sync", workflow.Name);
        Assert.Equal("Synchronizes customers", workflow.Description);
        Assert.Equal(CreatedAt, workflow.CreatedAtUtc);
        Assert.Equal(CreatedAt, workflow.UpdatedAtUtc);
        Assert.Null(workflow.PublishedVersionId);
    }

    [Fact]
    public void DraftEditsAdvanceRevisionButOnlyOneCandidateVersion()
    {
        var workflow = CreateWorkflow();

        workflow.UpdateGraph("{\"revision\":2}", CreatedAt.AddMinutes(1));
        workflow.UpdateGraph("{\"revision\":3}", CreatedAt.AddMinutes(2));

        Assert.Equal(1, workflow.Version);
        Assert.Equal(3, workflow.DraftRevision);
        Assert.Equal(WorkflowStatus.Draft, workflow.Status);
    }

    [Fact]
    public void PublishPointsAtMatchingImmutableSnapshot()
    {
        var workflow = CreateWorkflow();
        workflow.SetSchedule(
            "*/5 * * * *",
            "UTC",
            nextRunAtUtc: null,
            CreatedAt.AddMinutes(1));
        var snapshot = Snapshot(workflow, CreatedAt.AddMinutes(2));
        var nextRun = CreatedAt.AddMinutes(5);

        workflow.Publish(snapshot, nextRun);

        Assert.Equal(WorkflowStatus.Published, workflow.Status);
        Assert.Equal(snapshot.Id, workflow.PublishedVersionId);
        Assert.Equal(1, workflow.PublishedVersionNumber);
        Assert.Equal(nextRun, workflow.NextRunAtUtc);
        Assert.Equal(snapshot.PublishedAtUtc, workflow.UpdatedAtUtc);
    }

    [Fact]
    public void PublishedGraphEditCreatesOneNewCandidateAndKeepsHistoricalPointer()
    {
        var workflow = PublishedWorkflow();
        var originalPublishedId = workflow.PublishedVersionId;

        workflow.UpdateGraph("{\"revision\":2}", CreatedAt.AddMinutes(3));
        workflow.UpdateGraph("{\"revision\":3}", CreatedAt.AddMinutes(4));

        Assert.Equal(WorkflowStatus.Draft, workflow.Status);
        Assert.Equal(2, workflow.Version);
        Assert.Equal(3, workflow.DraftRevision);
        Assert.Equal(originalPublishedId, workflow.PublishedVersionId);
        Assert.Null(workflow.NextRunAtUtc);
    }

    [Fact]
    public void PublishRejectsSnapshotForDifferentGraphOrWorkflow()
    {
        var workflow = CreateWorkflow();
        var other = CreateWorkflow();
        var otherSnapshot = Snapshot(other, CreatedAt.AddMinutes(1));
        var differentGraph = new WorkflowVersion(
            workflow.Id,
            workflow.Version,
            1,
            "{\"different\":true}",
            WorkflowVersion.ComputeChecksum("{\"different\":true}"),
            "DOMAIN\\publisher",
            CreatedAt.AddMinutes(1));

        Assert.Throws<ArgumentException>(() => workflow.Publish(otherSnapshot));
        Assert.Throws<ArgumentException>(() => workflow.Publish(differentGraph));
    }

    [Fact]
    public void PausedWorkflowCanOnlyResumeItsPublishedSnapshot()
    {
        var workflow = PublishedWorkflow();
        var snapshotId = workflow.PublishedVersionId!.Value;
        var original = Snapshot(workflow, CreatedAt.AddMinutes(2));
        Assert.Equal(snapshotId, original.Id);
        workflow.Pause(CreatedAt.AddMinutes(3));

        workflow.Publish(
            original,
            nextRunAtUtc: null,
            changedAtUtc: CreatedAt.AddMinutes(4));

        Assert.Equal(WorkflowStatus.Published, workflow.Status);
        Assert.Equal(snapshotId, workflow.PublishedVersionId);
    }

    [Fact]
    public void ScheduleAndLifecycleTransitionsAreGuarded()
    {
        var workflow = CreateWorkflow();

        Assert.Throws<InvalidOperationException>(() => workflow.Pause(CreatedAt.AddMinutes(1)));
        Assert.Throws<ArgumentException>(() => workflow.SetSchedule(
            null,
            "UTC",
            CreatedAt.AddMinutes(10),
            CreatedAt.AddMinutes(1)));
        Assert.Throws<InvalidOperationException>(() => workflow.SetSchedule(
            "0 * * * *",
            "UTC",
            CreatedAt.AddMinutes(10),
            CreatedAt.AddMinutes(1)));

        workflow.Archive(CreatedAt.AddMinutes(2));

        Assert.Equal(WorkflowStatus.Archived, workflow.Status);
        Assert.Equal(CreatedAt.AddMinutes(2), workflow.ArchivedAtUtc);
        Assert.Throws<InvalidOperationException>(() =>
            workflow.Rename("Cannot change", CreatedAt.AddMinutes(3)));
        Assert.Throws<InvalidOperationException>(() =>
            workflow.UpdateGraph("{}", CreatedAt.AddMinutes(3)));
    }

    [Fact]
    public void MutationsRejectTimestampsThatMoveBackwards()
    {
        var workflow = CreateWorkflow();
        workflow.SetDescription("new", CreatedAt.AddMinutes(2));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            workflow.Rename("Old write", CreatedAt.AddMinutes(1)));
    }

    [Fact]
    public void WorkflowVersionIsImmutableAndValidatesChecksum()
    {
        const string graph = "{\"schemaVersion\":1}";
        var workflowId = Guid.NewGuid();
        var version = new WorkflowVersion(
            workflowId,
            7,
            1,
            graph,
            WorkflowVersion.ComputeChecksum(graph).ToLowerInvariant(),
            "DOMAIN\\publisher",
            CreatedAt);

        Assert.Equal(WorkflowVersion.CreateId(workflowId, 7), version.Id);
        Assert.Equal(64, version.Checksum.Length);
        Assert.All(
            typeof(WorkflowVersion).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => Assert.False(property.SetMethod?.IsPublic ?? false));
        Assert.Throws<ArgumentException>(() => new WorkflowVersion(
            workflowId,
            8,
            1,
            graph,
            new string('0', 64),
            "DOMAIN\\publisher",
            CreatedAt));
    }

    private static WorkflowDefinition CreateWorkflow() =>
        new("Customer sync", "DOMAIN\\owner", "{}", CreatedAt);

    private static WorkflowDefinition PublishedWorkflow()
    {
        var workflow = CreateWorkflow();
        workflow.Publish(Snapshot(workflow, CreatedAt.AddMinutes(2)));
        return workflow;
    }

    private static WorkflowVersion Snapshot(
        WorkflowDefinition workflow,
        DateTimeOffset publishedAtUtc) =>
        new(
            workflow.Id,
            workflow.Version,
            1,
            workflow.GraphJson,
            WorkflowVersion.ComputeChecksum(workflow.GraphJson),
            "DOMAIN\\publisher",
            publishedAtUtc);
}
