using THub.Application.Scheduling;
using THub.Application.Workflows;
using THub.Application.Workflows.Management;
using THub.Domain.Workflows;

namespace THub.Application.Tests;

public sealed class WorkflowCatalogServiceTests
{
    private readonly FakeWorkflowManagementRepository repository = new();
    private readonly WorkflowManagementTimeProvider timeProvider =
        new(WorkflowManagementTestData.Now);

    [Fact]
    public async Task ListsABoundedNormalizedPage()
    {
        var workflow = WorkflowManagementTestData.CreateDraft();
        repository.ListPage = new(
            [new(
                workflow.Id,
                workflow.Name,
                workflow.Description,
                workflow.Owner,
                workflow.Status,
                workflow.Version,
                workflow.DraftRevision,
                workflow.PublishedVersionNumber,
                workflow.CronExpression,
                workflow.TimeZoneId,
                workflow.NextRunAtUtc,
                workflow.UpdatedAtUtc)],
            1);
        using var cancellation = new CancellationTokenSource();

        var result = await CreateService().ListAsync(
            new(5, 20, "  Orders  ", WorkflowStatus.Draft),
            cancellation.Token);

        Assert.True(result.IsSuccess);
        Assert.Equal("Orders", Assert.Single(result.Value!.Items).Name);
        Assert.Equal(5, result.Value.Offset);
        Assert.Equal("Orders", repository.ReceivedListFilter!.Search);
        Assert.Equal(WorkflowStatus.Draft, repository.ReceivedListFilter.Status);
    }

    [Theory]
    [InlineData(-1, 20)]
    [InlineData(0, 0)]
    [InlineData(0, 101)]
    public async Task RejectsInvalidListBoundsWithoutReadingStorage(int offset, int limit)
    {
        var result = await CreateService().ListAsync(new(offset, limit));

        Assert.Equal(WorkflowOperationStatus.ValidationFailed, result.Status);
        Assert.Equal(0, repository.ListCalls);
    }

    [Fact]
    public async Task LoadReturnsStructuredNotFound()
    {
        var result = await CreateService().LoadAsync(Guid.NewGuid());

        Assert.Equal(WorkflowOperationStatus.NotFound, result.Status);
        Assert.Equal("workflow.not-found", Assert.Single(result.Issues).Code);
    }

    [Fact]
    public async Task LoadAllowsStructurallyIncompleteDraftGraph()
    {
        repository.Workflow = WorkflowManagementTestData.CreateDraft(
            "{\"schemaVersion\":2,\"variables\":[],\"functions\":[],\"nodes\":[],\"edges\":[]}");

        var result = await CreateService().LoadAsync(repository.Workflow.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            "{\"schemaVersion\":2,\"variables\":[],\"functions\":[],\"nodes\":[],\"edges\":[]}",
            result.Value!.GraphJson);
    }

    [Fact]
    public async Task LoadPropagatesCancellationToStorage()
    {
        repository.Workflow = WorkflowManagementTestData.CreateDraft();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService().LoadAsync(repository.Workflow.Id, cancellation.Token));
    }

    [Fact]
    public async Task CreatesCanonicalValidatedWorkflowAndSchedule()
    {
        var result = await CreateService().CreateAsync(new(
            " Orders ",
            " Daily transfer ",
            " CONTOSO\\owner ",
            WorkflowManagementTestData.ValidGraphJson,
            " */5 * * * * ",
            TimeZoneInfo.Utc.Id));

        Assert.True(result.IsSuccess);
        var stored = Assert.IsType<WorkflowDefinition>(repository.CreatedWorkflow);
        Assert.Equal("Orders", stored.Name);
        Assert.Equal("Daily transfer", stored.Description);
        Assert.Equal(WorkflowManagementTestData.CanonicalGraphJson, stored.GraphJson);
        Assert.Equal("*/5 * * * *", stored.CronExpression);
        Assert.Null(stored.NextRunAtUtc);
        Assert.Equal(WorkflowManagementTestData.Now, stored.CreatedAtUtc);
        Assert.Equal(1, repository.CreateCalls);
    }

    [Theory]
    [InlineData("not-json", "graph.invalid-json")]
    public async Task CreateRejectsMalformedGraphs(
        string graphJson,
        string expectedCode)
    {
        var result = await CreateService().CreateAsync(new(
            "Orders",
            null,
            "CONTOSO\\owner",
            graphJson));

        Assert.Equal(WorkflowOperationStatus.ValidationFailed, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == expectedCode);
        Assert.Equal(0, repository.CreateCalls);
    }

    [Fact]
    public async Task CreateAllowsStructurallyIncompleteDraftForProgressiveEditing()
    {
        var result = await CreateService().CreateAsync(new(
            "Orders",
            null,
            "CONTOSO\\owner",
            "{\"schemaVersion\":2,\"variables\":[],\"functions\":[],\"nodes\":[],\"edges\":[]}"));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, repository.CreateCalls);
    }

    [Fact]
    public async Task CreateRejectsInvalidCronWithoutWriting()
    {
        var result = await CreateService().CreateAsync(new(
            "Orders",
            null,
            "CONTOSO\\owner",
            WorkflowManagementTestData.ValidGraphJson,
            "not a cron",
            TimeZoneInfo.Utc.Id));

        Assert.Equal(WorkflowOperationStatus.ValidationFailed, result.Status);
        Assert.Equal("schedule.cron.invalid", Assert.Single(result.Issues).Code);
        Assert.Equal(0, repository.CreateCalls);
    }

    [Fact]
    public async Task SaveRejectsStaleDraftRevisionBeforeMutation()
    {
        repository.Workflow = WorkflowManagementTestData.CreateDraft();
        var originalName = repository.Workflow.Name;

        var result = await CreateService().SaveAsync(new(
            repository.Workflow.Id,
            ExpectedDraftRevision: 99,
            "Changed",
            repository.Workflow.Description,
            repository.Workflow.Owner,
            WorkflowManagementTestData.ValidGraphJson,
            repository.Workflow.CronExpression,
            repository.Workflow.TimeZoneId));

        Assert.Equal(WorkflowOperationStatus.ConcurrencyConflict, result.Status);
        Assert.Equal(originalName, repository.Workflow.Name);
        Assert.Contains("current draft revision is 1", result.Issues[0].Message, StringComparison.Ordinal);
        Assert.Equal(0, repository.SaveCalls);
    }

    [Fact]
    public async Task SavePublishedMetadataKeepsVersionAndRecomputesSchedule()
    {
        var published = WorkflowManagementTestData.CreatePublished();
        repository.Workflow = published.Workflow;
        repository.Version = published.Version;
        var startingRevision = published.Workflow.DraftRevision;

        var result = await CreateService().SaveAsync(new(
            published.Workflow.Id,
            startingRevision,
            "Renamed orders",
            published.Workflow.Description,
            published.Workflow.Owner,
            published.Workflow.GraphJson,
            "*/15 * * * *",
            TimeZoneInfo.Utc.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkflowStatus.Published, repository.SavedWorkflow!.Status);
        Assert.Equal(published.Version.Id, repository.SavedWorkflow.PublishedVersionId);
        Assert.Equal(WorkflowManagementTestData.Now.AddMinutes(15), repository.SavedWorkflow.NextRunAtUtc);
        Assert.Equal(2, repository.SavedWorkflow.DraftRevision);
        Assert.Equal(startingRevision, repository.ReceivedExpectedDraftRevision);
    }

    [Fact]
    public async Task SaveGraphChangeCreatesDraftAndClearsNextOccurrence()
    {
        var published = WorkflowManagementTestData.CreatePublished();
        repository.Workflow = published.Workflow;
        var updatedGraph = WorkflowManagementTestData.ValidGraphJson.Replace(
            "\"name\": \"Target\"",
            "\"name\": \"Updated target\"",
            StringComparison.Ordinal);

        var result = await CreateService().SaveAsync(new(
            published.Workflow.Id,
            published.Workflow.DraftRevision,
            published.Workflow.Name,
            published.Workflow.Description,
            published.Workflow.Owner,
            updatedGraph,
            published.Workflow.CronExpression,
            published.Workflow.TimeZoneId));

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkflowStatus.Draft, repository.SavedWorkflow!.Status);
        Assert.Equal(2, repository.SavedWorkflow.DraftRevision);
        Assert.Equal(2, repository.SavedWorkflow.Version);
        Assert.Null(repository.SavedWorkflow.NextRunAtUtc);
    }

    [Fact]
    public async Task PublishCreatesImmutableCanonicalSnapshotAndNextOccurrence()
    {
        repository.Workflow = WorkflowManagementTestData.CreateDraft(
            WorkflowManagementTestData.ValidGraphJson);
        var originalRevision = repository.Workflow.DraftRevision;

        var result = await CreateService().PublishAsync(new(
            repository.Workflow.Id,
            originalRevision,
            "CONTOSO\\publisher"));

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.CreatedNewVersion);
        Assert.Equal(WorkflowStatus.Published, repository.PublishedWorkflow!.Status);
        Assert.Equal(WorkflowManagementTestData.Now.AddMinutes(5), repository.PublishedWorkflow.NextRunAtUtc);
        Assert.Equal(WorkflowManagementTestData.CanonicalGraphJson, repository.PublishedVersion!.GraphJson);
        Assert.Equal(
            WorkflowVersion.ComputeChecksum(repository.PublishedVersion.GraphJson),
            repository.PublishedVersion.Checksum);
        Assert.Equal(originalRevision, repository.ReceivedExpectedDraftRevision);
        Assert.Equal(1, repository.PublishCalls);
    }

    [Fact]
    public async Task PublishRejectsInvalidPersistedDraftGraph()
    {
        repository.Workflow = WorkflowManagementTestData.CreateDraft(
            "{\"schemaVersion\":2,\"variables\":[],\"functions\":[],\"nodes\":[],\"edges\":[]}");

        var result = await CreateService().PublishAsync(new(
            repository.Workflow.Id,
            repository.Workflow.DraftRevision,
            "CONTOSO\\publisher"));

        Assert.Equal(WorkflowOperationStatus.ValidationFailed, result.Status);
        Assert.Equal("graph.empty", Assert.Single(result.Issues).Code);
        Assert.Equal(0, repository.PublishCalls);
    }

    [Fact]
    public async Task PublishResumesPausedWorkflowWithoutDuplicatingVersion()
    {
        var published = WorkflowManagementTestData.CreatePublished();
        published.Workflow.Pause(WorkflowManagementTestData.Now.AddMinutes(-20));
        repository.Workflow = published.Workflow;
        repository.Version = published.Version;

        var result = await CreateService().PublishAsync(new(
            published.Workflow.Id,
            published.Workflow.DraftRevision,
            "CONTOSO\\operator"));

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.CreatedNewVersion);
        Assert.Equal(WorkflowStatus.Published, repository.ResumedWorkflow!.Status);
        Assert.Equal(1, repository.ResumeCalls);
        Assert.Equal(0, repository.PublishCalls);
    }

    [Fact]
    public async Task MapsTransactionalStoreConcurrencyResult()
    {
        repository.Workflow = WorkflowManagementTestData.CreateDraft(
            WorkflowManagementTestData.CanonicalGraphJson);
        repository.PublishResult = WorkflowStoreWriteResult.Concurrency(3);

        var result = await CreateService().PublishAsync(new(
            repository.Workflow.Id,
            repository.Workflow.DraftRevision,
            "CONTOSO\\publisher"));

        Assert.Equal(WorkflowOperationStatus.ConcurrencyConflict, result.Status);
        Assert.Contains("current draft revision is 3", result.Issues[0].Message, StringComparison.Ordinal);
    }

    private WorkflowCatalogService CreateService() => new(
        repository,
        new WorkflowGraphSerializer(),
        new WorkflowGraphValidator(),
        new ScheduleCalculator(),
        timeProvider);
}
