using THub.Application.Workflows;
using THub.Application.Workflows.Management;
using THub.Domain.Runs;
using THub.Domain.Workflows;

namespace THub.Application.Tests;

internal static class WorkflowManagementTestData
{
    public static readonly DateTimeOffset Now =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    public const string ValidGraphJson = """
        {
          "schemaVersion": 1,
          "nodes": [
            {
              "id": "source",
              "kind": "SqlSource",
              "name": "Source",
              "x": 0,
              "y": 0,
              "settings": {
                "connectionId": "11111111-1111-1111-1111-111111111111",
                "schema": "dbo",
                "object": "SourceOrders",
                "batchSize": 1000
              }
            },
            {
              "id": "target",
              "kind": "SqlTarget",
              "name": "Target",
              "x": 300,
              "y": 0,
              "settings": {
                "connectionId": "22222222-2222-2222-2222-222222222222",
                "schema": "dbo",
                "object": "TargetOrders",
                "mode": "insert"
              }
            }
          ],
          "edges": [
            { "fromNodeId": "source", "toNodeId": "target" }
          ]
        }
        """;

    public static string CanonicalGraphJson { get; } =
        new WorkflowGraphSerializer().Serialize(
            new WorkflowGraphSerializer().Deserialize(ValidGraphJson));

    public static WorkflowDefinition CreateDraft(
        string graphJson = ValidGraphJson,
        string? cronExpression = "*/5 * * * *")
    {
        var createdAt = Now.AddHours(-1);
        var workflow = new WorkflowDefinition(
            "Orders",
            "CONTOSO\\workflow-owner",
            graphJson,
            createdAt,
            "Order transfer");
        workflow.SetSchedule(
            cronExpression,
            TimeZoneInfo.Utc.Id,
            nextRunAtUtc: null,
            createdAt);
        return workflow;
    }

    public static (WorkflowDefinition Workflow, WorkflowVersion Version) CreatePublished(
        string? cronExpression = "*/5 * * * *")
    {
        var workflow = CreateDraft(CanonicalGraphJson, cronExpression);
        var publishedAt = Now.AddMinutes(-30);
        var version = new WorkflowVersion(
            workflow.Id,
            workflow.Version,
            WorkflowGraphSerializer.CurrentSchemaVersion,
            workflow.GraphJson,
            WorkflowVersion.ComputeChecksum(workflow.GraphJson),
            "CONTOSO\\publisher",
            publishedAt);
        workflow.Publish(
            version,
            cronExpression is null ? null : Now.AddMinutes(5),
            publishedAt);
        return (workflow, version);
    }
}

internal sealed class WorkflowManagementTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}

internal sealed class FakeWorkflowManagementRepository : IWorkflowManagementRepository
{
    public WorkflowListPage ListPage { get; set; } = new([], 0);

    public WorkflowDefinition? Workflow { get; set; }

    public WorkflowVersion? Version { get; set; }

    public WorkflowRun? Run { get; set; }

    public WorkflowStoreWriteResult CreateResult { get; set; } =
        WorkflowStoreWriteResult.Success;

    public WorkflowStoreWriteResult SaveResult { get; set; } =
        WorkflowStoreWriteResult.Success;

    public WorkflowStoreWriteResult PublishResult { get; set; } =
        WorkflowStoreWriteResult.Success;

    public WorkflowStoreWriteResult ResumeResult { get; set; } =
        WorkflowStoreWriteResult.Success;

    public WorkflowStoreWriteResult QueueResult { get; set; } =
        WorkflowStoreWriteResult.Success;

    public WorkflowStoreWriteResult CancellationResult { get; set; } =
        WorkflowStoreWriteResult.Success;

    public WorkflowListFilter? ReceivedListFilter { get; private set; }

    public WorkflowDefinition? CreatedWorkflow { get; private set; }

    public WorkflowDefinition? SavedWorkflow { get; private set; }

    public WorkflowDefinition? PublishedWorkflow { get; private set; }

    public WorkflowVersion? PublishedVersion { get; private set; }

    public WorkflowDefinition? ResumedWorkflow { get; private set; }

    public WorkflowRun? QueuedRun { get; private set; }

    public WorkflowRun? CancelledRun { get; private set; }

    public int? ReceivedExpectedDraftRevision { get; private set; }

    public Guid? ReceivedExpectedVersionId { get; private set; }

    public WorkflowRunStatus? ReceivedExpectedRunStatus { get; private set; }

    public int ListCalls { get; private set; }

    public int GetWorkflowCalls { get; private set; }

    public int GetVersionCalls { get; private set; }

    public int CreateCalls { get; private set; }

    public int SaveCalls { get; private set; }

    public int PublishCalls { get; private set; }

    public int ResumeCalls { get; private set; }

    public int QueueCalls { get; private set; }

    public int CancellationCalls { get; private set; }

    public Task<WorkflowListPage> ListWorkflowsAsync(
        WorkflowListFilter filter,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ListCalls++;
        ReceivedListFilter = filter;
        return Task.FromResult(ListPage);
    }

    public Task<WorkflowDefinition?> GetWorkflowAsync(
        Guid workflowId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetWorkflowCalls++;
        return Task.FromResult(
            Workflow?.Id == workflowId ? Workflow : null);
    }

    public Task<WorkflowVersion?> GetWorkflowVersionAsync(
        Guid workflowVersionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetVersionCalls++;
        return Task.FromResult(
            Version?.Id == workflowVersionId ? Version : null);
    }

    public Task<WorkflowRun?> GetRunAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Run?.Id == runId ? Run : null);
    }

    public Task<WorkflowStoreWriteResult> CreateWorkflowAsync(
        WorkflowDefinition workflow,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CreateCalls++;
        CreatedWorkflow = workflow;
        return Task.FromResult(CreateResult);
    }

    public Task<WorkflowStoreWriteResult> SaveWorkflowAsync(
        WorkflowDefinition workflow,
        int expectedDraftRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SaveCalls++;
        SavedWorkflow = workflow;
        ReceivedExpectedDraftRevision = expectedDraftRevision;
        return Task.FromResult(SaveResult);
    }

    public Task<WorkflowStoreWriteResult> PublishWorkflowAsync(
        WorkflowDefinition workflow,
        WorkflowVersion version,
        int expectedDraftRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PublishCalls++;
        PublishedWorkflow = workflow;
        PublishedVersion = version;
        ReceivedExpectedDraftRevision = expectedDraftRevision;
        return Task.FromResult(PublishResult);
    }

    public Task<WorkflowStoreWriteResult> ResumeWorkflowAsync(
        WorkflowDefinition workflow,
        int expectedDraftRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResumeCalls++;
        ResumedWorkflow = workflow;
        ReceivedExpectedDraftRevision = expectedDraftRevision;
        return Task.FromResult(ResumeResult);
    }

    public Task<WorkflowStoreWriteResult> QueueRunAsync(
        WorkflowRun run,
        Guid expectedWorkflowVersionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        QueueCalls++;
        QueuedRun = run;
        ReceivedExpectedVersionId = expectedWorkflowVersionId;
        return Task.FromResult(QueueResult);
    }

    public Task<WorkflowStoreWriteResult> SaveRunCancellationAsync(
        WorkflowRun run,
        WorkflowRunStatus expectedStatus,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancellationCalls++;
        CancelledRun = run;
        ReceivedExpectedRunStatus = expectedStatus;
        return Task.FromResult(CancellationResult);
    }
}
