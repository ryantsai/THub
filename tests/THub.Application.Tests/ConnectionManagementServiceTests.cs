using THub.Application.Connections;
using THub.Domain.Connections;

namespace THub.Application.Tests;

public sealed class ConnectionManagementServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreatesListsAndProbesConnection()
    {
        var store = new FakeStore();
        var service = CreateService(store);
        var configuration = new SqlServerConnectionConfiguration("sql01", "Warehouse");

        var created = await service.CreateAsync(
            "Warehouse",
            configuration,
            "DOMAIN\\admin",
            Now,
            CancellationToken.None);
        var listed = await service.ListAsync(CancellationToken.None);
        var probe = await service.ProbeAsync(created.Connection!.Id, CancellationToken.None);

        Assert.Equal(ConnectionSaveStatus.Saved, created.Status);
        Assert.Single(listed);
        Assert.True(probe.IsSuccessful);
    }

    [Fact]
    public async Task StaleUpdateReturnsConflictWithoutMutation()
    {
        var store = new FakeStore();
        var service = CreateService(store);
        var created = await service.CreateAsync(
            "Warehouse",
            new SqlServerConnectionConfiguration("sql01", "Warehouse"),
            "DOMAIN\\admin",
            Now,
            CancellationToken.None);

        var result = await service.UpdateAsync(
            created.Connection!.Id,
            "Changed",
            created.Connection.Configuration,
            Now.AddMinutes(-1),
            Now.AddMinutes(1),
            CancellationToken.None);

        Assert.Equal(ConnectionSaveStatus.Conflict, result.Status);
        Assert.Equal("Warehouse", (await service.ListAsync(CancellationToken.None)).Single().Name);
    }

    [Fact]
    public async Task DuplicateNameIsStructuredConflict()
    {
        var store = new FakeStore();
        var service = CreateService(store);
        var configuration = new FileConnectionConfiguration(ConnectionKind.CsvFile, "D:\\Inbound");
        await service.CreateAsync(
            "Inbound",
            configuration,
            "DOMAIN\\admin",
            Now,
            CancellationToken.None);

        var duplicate = await service.CreateAsync(
            "inbound",
            configuration,
            "DOMAIN\\admin",
            Now,
            CancellationToken.None);

        Assert.Equal(ConnectionSaveStatus.DuplicateName, duplicate.Status);
        Assert.Equal("connection.name.duplicate", duplicate.ErrorCode);
    }

    private static ConnectionManagementService CreateService(FakeStore store) => new(
        store,
        new FakeProbe(),
        new ConnectionConfigurationSerializer());

    private sealed class FakeStore : IDataConnectionStore
    {
        private readonly Dictionary<Guid, DataConnection> values = [];

        public Task<IReadOnlyList<DataConnection>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DataConnection>>(values.Values.ToArray());

        public Task<DataConnection?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(values.GetValueOrDefault(id));

        public Task<ConnectionSaveStatus> AddAsync(
            DataConnection connection,
            CancellationToken cancellationToken)
        {
            if (values.Values.Any(existing =>
                    string.Equals(existing.Name, connection.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromResult(ConnectionSaveStatus.DuplicateName);
            }

            values.Add(connection.Id, connection);
            return Task.FromResult(ConnectionSaveStatus.Saved);
        }

        public Task<ConnectionSaveStatus> SaveAsync(
            DataConnection connection,
            DateTimeOffset expectedUpdatedAtUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult(ConnectionSaveStatus.Saved);
    }

    private sealed class FakeProbe : IDataConnectionProbe
    {
        public Task<ConnectionProbeResult> ProbeAsync(
            DataConnection connection,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectionProbeResult(
                true,
                TimeSpan.FromMilliseconds(1),
                "Connected."));
    }
}
