using THub.Application.Connections;
using THub.Application.Execution;
using THub.Application.Workflows;
using THub.Domain.Connections;

namespace THub.Application.Tests;

public sealed class WorkflowSchemaInspectionServiceTests
{
    [Fact]
    public async Task InspectAsync_UsesEnabledMatchingConnectionAndPreservesTargetIntent()
    {
        var connection = Connection(ConnectionKind.PostgreSql);
        var inspector = new StubInspector();
        var service = new WorkflowSchemaInspectionService(
            new StubConnectionStore(connection),
            inspector);

        var result = await service.InspectAsync(
            connection.Id,
            ConnectionKind.PostgreSql,
            "public",
            "orders",
            requireWritableTable: true,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(inspector.RequireWritableTable);
        Assert.Equal(connection.Id, result.Schema!.ConnectionId);
        Assert.Equal("Id", Assert.Single(result.Schema.Columns).Name);
    }

    [Fact]
    public async Task InspectAsync_RejectsMismatchedConnectionKindWithoutCallingAdapter()
    {
        var connection = Connection(ConnectionKind.MySql);
        var inspector = new StubInspector();
        var service = new WorkflowSchemaInspectionService(
            new StubConnectionStore(connection),
            inspector);

        var result = await service.InspectAsync(
            connection.Id,
            ConnectionKind.Oracle,
            "APP",
            "ORDERS",
            requireWritableTable: false,
            CancellationToken.None);

        Assert.Equal(WorkflowSchemaInspectionStatus.Invalid, result.Status);
        Assert.False(inspector.WasCalled);
    }

    [Theory]
    [InlineData("", "orders")]
    [InlineData("public", "")]
    public async Task InspectAsync_RejectsIncompleteObjectIdentity(string schema, string objectName)
    {
        var inspector = new StubInspector();
        var service = new WorkflowSchemaInspectionService(
            new StubConnectionStore(null),
            inspector);

        var result = await service.InspectAsync(
            Guid.NewGuid(),
            ConnectionKind.PostgreSql,
            schema,
            objectName,
            requireWritableTable: false,
            CancellationToken.None);

        Assert.Equal(WorkflowSchemaInspectionStatus.Invalid, result.Status);
        Assert.False(inspector.WasCalled);
    }

    private static DataConnection Connection(ConnectionKind kind) =>
        new(
            "warehouse",
            kind,
            """{"schemaVersion":1}""",
            "tester",
            DateTimeOffset.UtcNow);

    private sealed class StubInspector : IWorkflowSchemaInspector
    {
        public bool WasCalled { get; private set; }
        public bool RequireWritableTable { get; private set; }

        public Task<WorkflowSchemaInspectionResult> InspectAsync(
            DataConnection connection,
            string schema,
            string objectName,
            bool requireWritableTable,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            RequireWritableTable = requireWritableTable;
            return Task.FromResult(new WorkflowSchemaInspectionResult(
                WorkflowSchemaInspectionStatus.Success,
                new WorkflowObjectSchemaDto(
                    connection.Id,
                    schema,
                    objectName,
                    [new("Id", "bigint", TabularDataType.Int64, false, true, true)]),
                "Loaded."));
        }
    }

    private sealed class StubConnectionStore(DataConnection? connection) : IDataConnectionStore
    {
        public Task<IReadOnlyList<DataConnection>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DataConnection>>(
                connection is null ? [] : [connection]);

        public Task<DataConnection?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(connection?.Id == id ? connection : null);

        public Task<ConnectionSaveStatus> AddAsync(
            DataConnection connection,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ConnectionSaveStatus> SaveAsync(
            DataConnection connection,
            DateTimeOffset expectedUpdatedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
