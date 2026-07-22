using THub.Domain.Connections;

namespace THub.Application.Connections;

public enum ConnectionSaveStatus
{
    Saved,
    NotFound,
    Conflict,
    DuplicateName
}

public sealed record ConnectionSummary(
    Guid Id,
    string Name,
    ConnectionKind Kind,
    bool IsEnabled,
    DateTimeOffset UpdatedAtUtc);

public sealed record ConnectionDetails(
    Guid Id,
    string Name,
    ConnectionKind Kind,
    ConnectionConfiguration Configuration,
    bool IsEnabled,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ConnectionCommandResult(
    ConnectionSaveStatus Status,
    ConnectionDetails? Connection,
    string? ErrorCode = null,
    string? Message = null);

public sealed record ConnectionProbeResult(
    bool IsSuccessful,
    TimeSpan Elapsed,
    string Message,
    string? ServerVersion = null);

public interface IDataConnectionStore
{
    Task<IReadOnlyList<DataConnection>> ListAsync(CancellationToken cancellationToken);

    Task<DataConnection?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<ConnectionSaveStatus> AddAsync(
        DataConnection connection,
        CancellationToken cancellationToken);

    Task<ConnectionSaveStatus> SaveAsync(
        DataConnection connection,
        DateTimeOffset expectedUpdatedAtUtc,
        CancellationToken cancellationToken);
}

public interface IDataConnectionProbe
{
    Task<ConnectionProbeResult> ProbeAsync(
        DataConnection connection,
        CancellationToken cancellationToken);
}

public sealed class ConnectionManagementService(
    IDataConnectionStore store,
    IDataConnectionProbe probe,
    ConnectionConfigurationSerializer serializer)
{
    public async Task<IReadOnlyList<ConnectionSummary>> ListAsync(
        CancellationToken cancellationToken)
    {
        var connections = await store.ListAsync(cancellationToken);
        return connections
            .Select(connection => new ConnectionSummary(
                connection.Id,
                connection.Name,
                connection.Kind,
                connection.IsEnabled,
                connection.UpdatedAtUtc))
            .ToArray();
    }

    public async Task<ConnectionDetails?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var connection = await store.FindAsync(id, cancellationToken);
        return connection is null ? null : ToDetails(connection);
    }

    public async Task<ConnectionCommandResult> CreateAsync(
        string name,
        ConnectionConfiguration configuration,
        string actor,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connection = new DataConnection(
            name,
            configuration.Kind,
            serializer.Serialize(configuration),
            actor,
            createdAtUtc);
        var status = await store.AddAsync(connection, cancellationToken);
        return status == ConnectionSaveStatus.Saved
            ? new ConnectionCommandResult(status, ToDetails(connection))
            : new ConnectionCommandResult(
                status,
                null,
                "connection.name.duplicate",
                "A connection with that name already exists.");
    }

    public async Task<ConnectionCommandResult> UpdateAsync(
        Guid id,
        string name,
        ConnectionConfiguration configuration,
        DateTimeOffset expectedUpdatedAtUtc,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connection = await store.FindAsync(id, cancellationToken);
        if (connection is null)
        {
            return new ConnectionCommandResult(
                ConnectionSaveStatus.NotFound,
                null,
                "connection.not_found",
                "The connection no longer exists.");
        }
        if (connection.Kind != configuration.Kind)
        {
            return new ConnectionCommandResult(
                ConnectionSaveStatus.Conflict,
                null,
                "connection.kind.immutable",
                "Create a new connection to change its connector kind.");
        }
        if (connection.UpdatedAtUtc != expectedUpdatedAtUtc.ToUniversalTime())
        {
            return new ConnectionCommandResult(
                ConnectionSaveStatus.Conflict,
                ToDetails(connection),
                "connection.concurrency",
                "The connection was changed by another user. Reload before saving.");
        }

        connection.Rename(name, changedAtUtc);
        connection.UpdateConfiguration(serializer.Serialize(configuration), changedAtUtc);
        var status = await store.SaveAsync(connection, expectedUpdatedAtUtc, cancellationToken);
        return status == ConnectionSaveStatus.Saved
            ? new ConnectionCommandResult(status, ToDetails(connection))
            : new ConnectionCommandResult(
                status,
                null,
                status == ConnectionSaveStatus.DuplicateName
                    ? "connection.name.duplicate"
                    : "connection.concurrency",
                status == ConnectionSaveStatus.DuplicateName
                    ? "A connection with that name already exists."
                    : "The connection was changed by another user. Reload before saving.");
    }

    public async Task<ConnectionCommandResult> SetEnabledAsync(
        Guid id,
        bool isEnabled,
        DateTimeOffset expectedUpdatedAtUtc,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken)
    {
        var connection = await store.FindAsync(id, cancellationToken);
        if (connection is null)
        {
            return new ConnectionCommandResult(ConnectionSaveStatus.NotFound, null);
        }
        if (connection.UpdatedAtUtc != expectedUpdatedAtUtc.ToUniversalTime())
        {
            return new ConnectionCommandResult(
                ConnectionSaveStatus.Conflict,
                ToDetails(connection),
                "connection.concurrency");
        }

        if (isEnabled)
        {
            connection.Enable(changedAtUtc);
        }
        else
        {
            connection.Disable(changedAtUtc);
        }

        var status = await store.SaveAsync(connection, expectedUpdatedAtUtc, cancellationToken);
        return new ConnectionCommandResult(
            status,
            status == ConnectionSaveStatus.Saved ? ToDetails(connection) : null,
            status == ConnectionSaveStatus.Saved ? null : "connection.concurrency");
    }

    public async Task<ConnectionProbeResult> ProbeAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var connection = await store.FindAsync(id, cancellationToken);
        if (connection is null)
        {
            return new ConnectionProbeResult(
                false,
                TimeSpan.Zero,
                "The connection no longer exists.");
        }
        if (!connection.IsEnabled)
        {
            return new ConnectionProbeResult(
                false,
                TimeSpan.Zero,
                "The connection is disabled.");
        }

        return await probe.ProbeAsync(connection, cancellationToken);
    }

    private ConnectionDetails ToDetails(DataConnection connection) => new(
        connection.Id,
        connection.Name,
        connection.Kind,
        serializer.Deserialize(connection),
        connection.IsEnabled,
        connection.CreatedBy,
        connection.CreatedAtUtc,
        connection.UpdatedAtUtc);
}
