using THub.Application.Connections;
using THub.Application.Publications;
using THub.Domain.Connections;
using THub.Domain.Publications;

namespace THub.Application.Tests;

public sealed class PublicationSourceInspectionServiceTests
{
    [Fact]
    public async Task InspectObjectAsync_ReturnsDiscoveredImmutableMetadata()
    {
        var connection = CreateConnection();
        var connections = new ConnectionStore(connection);
        var expected = new PublicationSourceObjectInspectionDto(
            connection.Id,
            "dbo",
            "Orders",
            PublicationSourceObjectKind.Table,
            "fingerprint",
            [new PublicationSourceColumnDto(
                0, "OrderId", "int", PublicationDataType.Int32, true, false, false, false, true, 0, null, null, null)],
            []);
        var inspector = new Inspector
        {
            Inspection = new(PublicationSourceInspectionStatus.Success, expected),
        };
        var service = new PublicationSourceInspectionService(connections, inspector);

        var result = await service.InspectObjectAsync(
            connection.Id,
            "dbo",
            "Orders",
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("fingerprint", result.Value!.SchemaFingerprint);
        Assert.Equal("dbo", inspector.Schema);
        Assert.Equal("Orders", inspector.ObjectName);
    }

    [Fact]
    public async Task ListObjectsAsync_RejectsUnboundedTakeBeforeInspecting()
    {
        var connection = CreateConnection();
        var inspector = new Inspector();
        var service = new PublicationSourceInspectionService(new ConnectionStore(connection), inspector);

        var result = await service.ListObjectsAsync(
            connection.Id,
            null,
            201,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(PublicationProblemKind.Validation, result.Problem!.Kind);
        Assert.Equal(0, inspector.Calls);
    }

    [Fact]
    public async Task InspectObjectAsync_RejectsDisabledConnection()
    {
        var connection = CreateConnection();
        connection.Disable(new DateTimeOffset(2026, 7, 23, 9, 0, 0, TimeSpan.Zero));
        var inspector = new Inspector();
        var service = new PublicationSourceInspectionService(new ConnectionStore(connection), inspector);

        var result = await service.InspectObjectAsync(
            connection.Id,
            "dbo",
            "Orders",
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(PublicationProblemKind.Conflict, result.Problem!.Kind);
        Assert.Equal(0, inspector.Calls);
    }

    [Theory]
    [InlineData(ConnectionKind.SqlServer)]
    [InlineData(ConnectionKind.MySql)]
    [InlineData(ConnectionKind.PostgreSql)]
    [InlineData(ConnectionKind.Oracle)]
    public async Task InspectObjectAsync_AcceptsEveryRelationalConnectionKind(
        ConnectionKind kind)
    {
        var connection = CreateConnection(kind);
        var expected = new PublicationSourceObjectInspectionDto(
            connection.Id,
            "app",
            "Orders",
            PublicationSourceObjectKind.Table,
            "fingerprint",
            [new PublicationSourceColumnDto(
                0, "OrderId", "integer", PublicationDataType.Int32, true, false, false, false, true, 0, null, null, null)],
            []);
        var inspector = new Inspector
        {
            Inspection = new(PublicationSourceInspectionStatus.Success, expected)
        };
        var service = new PublicationSourceInspectionService(
            new ConnectionStore(connection),
            inspector);

        var result = await service.InspectObjectAsync(
            connection.Id,
            "app",
            "Orders",
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, inspector.Calls);
    }

    private static DataConnection CreateConnection(
        ConnectionKind kind = ConnectionKind.SqlServer) => new(
        "Operations SQL",
        kind,
        "{}",
        "CONTOSO\\admin",
        new DateTimeOffset(2026, 7, 23, 8, 0, 0, TimeSpan.Zero));

    private sealed class ConnectionStore(DataConnection connection) : IDataConnectionStore
    {
        public Task<IReadOnlyList<DataConnection>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DataConnection>>([connection]);

        public Task<DataConnection?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<DataConnection?>(id == connection.Id ? connection : null);

        public Task<ConnectionSaveStatus> AddAsync(
            DataConnection value,
            CancellationToken cancellationToken) =>
            Task.FromResult(ConnectionSaveStatus.Saved);

        public Task<ConnectionSaveStatus> SaveAsync(
            DataConnection value,
            DateTimeOffset expectedUpdatedAtUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult(ConnectionSaveStatus.Saved);
    }

    private sealed class Inspector : IPublicationSourceSchemaInspector
    {
        public int Calls { get; private set; }

        public string? Schema { get; private set; }

        public string? ObjectName { get; private set; }

        public PublicationSourceInspectionResult<PublicationSourceObjectInspectionDto> Inspection { get; set; } =
            new(PublicationSourceInspectionStatus.NotFound, null);

        public Task<PublicationSourceInspectionResult<PublicationSourceObjectPageDto>> ListObjectsAsync(
            DataConnection connection,
            string? search,
            int take,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new PublicationSourceInspectionResult<PublicationSourceObjectPageDto>(
                PublicationSourceInspectionStatus.Success,
                new PublicationSourceObjectPageDto([], false)));
        }

        public Task<PublicationSourceInspectionResult<PublicationSourceObjectInspectionDto>> InspectObjectAsync(
            DataConnection connection,
            string schema,
            string objectName,
            CancellationToken cancellationToken)
        {
            Calls++;
            Schema = schema;
            ObjectName = objectName;
            return Task.FromResult(Inspection);
        }
    }
}
