using THub.Application.Connections;
using THub.Application.Scheduling;
using THub.Application.Workflows;
using THub.Application.Workflows.Management;
using THub.Domain.Connections;

namespace THub.Application.Tests;

public sealed class WorkflowPackageServiceTests
{
    [Fact]
    public async Task ExportThenImportCreatesANewUnpublishedDraft()
    {
        var repository = new FakeWorkflowManagementRepository
        {
            Workflow = WorkflowManagementTestData.CreateDraft()
        };
        var connections = new EmptyConnectionStore();
        var graphSerializer = new WorkflowGraphSerializer();
        var catalog = new WorkflowCatalogService(
            repository,
            graphSerializer,
            new WorkflowGraphValidator(),
            new ScheduleCalculator(),
            new WorkflowManagementTimeProvider(WorkflowManagementTestData.Now));
        var service = new WorkflowPackageService(
            repository,
            connections,
            catalog,
            graphSerializer,
            new WorkflowManagementTimeProvider(WorkflowManagementTestData.Now));

        var exported = await service.ExportAsync(repository.Workflow.Id);
        var imported = await service.ImportAsync(
            exported.Value!.Content,
            "CONTOSO\\importer");

        Assert.True(exported.IsSuccess);
        Assert.EndsWith(".thub-workflow.json", exported.Value.FileName, StringComparison.Ordinal);
        Assert.DoesNotContain("configurationJson", exported.Value.Content, StringComparison.OrdinalIgnoreCase);
        Assert.True(imported.IsSuccess);
        Assert.Equal(THub.Domain.Workflows.WorkflowStatus.Draft, imported.Value!.Workflow.Status);
        Assert.Equal("CONTOSO\\importer", imported.Value.Workflow.Owner);
        Assert.Equal(2, imported.Value.Warnings.Count);
        Assert.Equal(1, repository.CreateCalls);
    }

    [Fact]
    public async Task ImportRejectsUnknownPackageProperties()
    {
        var repository = new FakeWorkflowManagementRepository();
        var graphSerializer = new WorkflowGraphSerializer();
        var catalog = new WorkflowCatalogService(
            repository,
            graphSerializer,
            new WorkflowGraphValidator(),
            new ScheduleCalculator(),
            new WorkflowManagementTimeProvider(WorkflowManagementTestData.Now));
        var service = new WorkflowPackageService(
            repository,
            new EmptyConnectionStore(),
            catalog,
            graphSerializer,
            new WorkflowManagementTimeProvider(WorkflowManagementTestData.Now));

        var result = await service.ImportAsync(
            """{"packageSchemaVersion":1,"unexpected":true}""",
            "CONTOSO\\importer");

        Assert.Equal(WorkflowOperationStatus.ValidationFailed, result.Status);
        Assert.Equal("workflow.import.invalid-json", Assert.Single(result.Issues).Code);
        Assert.Equal(0, repository.CreateCalls);
    }

    private sealed class EmptyConnectionStore : IDataConnectionStore
    {
        public Task<IReadOnlyList<DataConnection>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DataConnection>>([]);

        public Task<DataConnection?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<DataConnection?>(null);

        public Task<ConnectionSaveStatus> AddAsync(
            DataConnection connection,
            CancellationToken cancellationToken) =>
            Task.FromResult(ConnectionSaveStatus.Saved);

        public Task<ConnectionSaveStatus> SaveAsync(
            DataConnection connection,
            DateTimeOffset expectedUpdatedAtUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult(ConnectionSaveStatus.Saved);
    }
}
