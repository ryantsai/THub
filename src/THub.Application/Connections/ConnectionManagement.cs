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
    bool HasStoredCredential,
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

    Task<bool> CredentialExistsAsync(
        string secretReference,
        CancellationToken cancellationToken) =>
        Task.FromResult(false);

    Task<ConnectionSaveStatus> AddAsync(
        DataConnection connection,
        CancellationToken cancellationToken);

    Task<ConnectionSaveStatus> AddAsync(
        DataConnection connection,
        ConnectionCredentialWrite? credentialWrite,
        CancellationToken cancellationToken) =>
        credentialWrite is null
            ? AddAsync(connection, cancellationToken)
            : throw new NotSupportedException(
                "This connection store does not support encrypted credential writes.");

    Task<ConnectionSaveStatus> SaveAsync(
        DataConnection connection,
        DateTimeOffset expectedUpdatedAtUtc,
        CancellationToken cancellationToken);

    Task<ConnectionSaveStatus> SaveAsync(
        DataConnection connection,
        DateTimeOffset expectedUpdatedAtUtc,
        ConnectionCredentialWrite? credentialWrite,
        CancellationToken cancellationToken) =>
        credentialWrite is null
            ? SaveAsync(connection, expectedUpdatedAtUtc, cancellationToken)
            : throw new NotSupportedException(
                "This connection store does not support encrypted credential writes.");

    Task<ConnectionSaveStatus> DeleteAsync(
        Guid id,
        DateTimeOffset expectedUpdatedAtUtc,
        DateTimeOffset deletedAtUtc,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "This connection store does not support connection deletion.");
}

public sealed record ConnectionCredentialWrite
{
    public ConnectionCredentialWrite(
        string secretReference,
        ConnectionCredential credential,
        DateTimeOffset changedAtUtc)
    {
        SecretReference = new DatabaseAuthenticationConfiguration(
            DatabaseAuthenticationKind.UserPassword,
            secretReference).CredentialSecretReference!;
        Credential = credential ?? throw new ArgumentNullException(nameof(credential));
        if (changedAtUtc == default)
        {
            throw new ArgumentOutOfRangeException(nameof(changedAtUtc));
        }

        ChangedAtUtc = changedAtUtc.ToUniversalTime();
    }

    public string SecretReference { get; }

    public ConnectionCredential Credential { get; }

    public DateTimeOffset ChangedAtUtc { get; }
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
        return connection is null ? null : await ToDetailsAsync(connection, cancellationToken);
    }

    public async Task<ConnectionCommandResult> CreateAsync(
        string name,
        ConnectionConfiguration configuration,
        string actor,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
        => await CreateAsync(
            name,
            configuration,
            credential: null,
            actor,
            createdAtUtc,
            cancellationToken);

    public async Task<ConnectionCommandResult> CreateAsync(
        string name,
        ConnectionConfiguration configuration,
        ConnectionCredential? credential,
        string actor,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var credentialWrite = CreateCredentialWrite(
            configuration,
            credential,
            createdAtUtc,
            isNewConnection: true,
            previousConfiguration: null);
        if (credentialWrite.RequiredCredentialMessage is not null)
        {
            return credentialWrite.RequiredCredentialMessage;
        }

        var connection = new DataConnection(
            name,
            configuration.Kind,
            serializer.Serialize(configuration),
            actor,
            createdAtUtc);
        var status = await store.AddAsync(
            connection,
            credentialWrite.Write,
            cancellationToken);
        return status == ConnectionSaveStatus.Saved
            ? new ConnectionCommandResult(
                status,
                await ToDetailsAsync(connection, cancellationToken))
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
        => await UpdateAsync(
            id,
            name,
            configuration,
            credential: null,
            expectedUpdatedAtUtc,
            changedAtUtc,
            cancellationToken);

    public async Task<ConnectionCommandResult> UpdateAsync(
        Guid id,
        string name,
        ConnectionConfiguration configuration,
        ConnectionCredential? credential,
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
                await ToDetailsAsync(connection, cancellationToken),
                "connection.concurrency",
                "The connection was changed by another user. Reload before saving.");
        }

        var previousConfiguration = serializer.Deserialize(connection);
        var credentialWrite = CreateCredentialWrite(
            configuration,
            credential,
            changedAtUtc,
            isNewConnection: false,
            previousConfiguration);
        if (credentialWrite.RequiredCredentialMessage is not null)
        {
            return credentialWrite.RequiredCredentialMessage;
        }

        var newReference = GetCredentialReference(configuration);
        if (newReference is not null &&
            credentialWrite.Write is null &&
            !await store.CredentialExistsAsync(newReference, cancellationToken))
        {
            return CredentialRequired();
        }

        connection.Rename(name, changedAtUtc);
        connection.UpdateConfiguration(serializer.Serialize(configuration), changedAtUtc);
        var status = await store.SaveAsync(
            connection,
            expectedUpdatedAtUtc,
            credentialWrite.Write,
            cancellationToken);
        return status == ConnectionSaveStatus.Saved
            ? new ConnectionCommandResult(
                status,
                await ToDetailsAsync(connection, cancellationToken))
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
                await ToDetailsAsync(connection, cancellationToken),
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

        var status = await store.SaveAsync(
            connection,
            expectedUpdatedAtUtc,
            credentialWrite: null,
            cancellationToken);
        return new ConnectionCommandResult(
            status,
            status == ConnectionSaveStatus.Saved
                ? await ToDetailsAsync(connection, cancellationToken)
                : null,
            status == ConnectionSaveStatus.Saved ? null : "connection.concurrency");
    }

    public async Task<ConnectionCommandResult> DeleteAsync(
        Guid id,
        DateTimeOffset expectedUpdatedAtUtc,
        DateTimeOffset deletedAtUtc,
        CancellationToken cancellationToken)
    {
        var status = await store.DeleteAsync(
            id,
            expectedUpdatedAtUtc,
            deletedAtUtc,
            cancellationToken);
        return status switch
        {
            ConnectionSaveStatus.Saved => new ConnectionCommandResult(status, null),
            ConnectionSaveStatus.NotFound => new ConnectionCommandResult(
                status,
                null,
                "connection.not_found",
                "The connection no longer exists."),
            _ => new ConnectionCommandResult(
                ConnectionSaveStatus.Conflict,
                null,
                "connection.concurrency",
                "The connection was changed by another user. Reload before deleting.")
        };
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

    private async Task<ConnectionDetails> ToDetailsAsync(
        DataConnection connection,
        CancellationToken cancellationToken)
    {
        var configuration = serializer.Deserialize(connection);
        var reference = GetCredentialReference(configuration);
        var hasStoredCredential = reference is not null &&
            await store.CredentialExistsAsync(reference, cancellationToken);
        return new ConnectionDetails(
            connection.Id,
            connection.Name,
            connection.Kind,
            configuration,
            hasStoredCredential,
            connection.IsEnabled,
            connection.CreatedBy,
            connection.CreatedAtUtc,
            connection.UpdatedAtUtc);
    }

    private static (
        ConnectionCredentialWrite? Write,
        ConnectionCommandResult? RequiredCredentialMessage) CreateCredentialWrite(
        ConnectionConfiguration configuration,
        ConnectionCredential? credential,
        DateTimeOffset changedAtUtc,
        bool isNewConnection,
        ConnectionConfiguration? previousConfiguration)
    {
        var reference = GetCredentialReference(configuration);
        if (reference is null)
        {
            if (credential is not null)
            {
                throw new ArgumentException(
                    "Integrated and local-file connections cannot store a username/password.",
                    nameof(credential));
            }

            return (null, null);
        }

        if (credential is not null)
        {
            return (new ConnectionCredentialWrite(reference, credential, changedAtUtc), null);
        }

        var previousReference = previousConfiguration is null
            ? null
            : GetCredentialReference(previousConfiguration);
        return isNewConnection ||
            !string.Equals(reference, previousReference, StringComparison.Ordinal)
                ? (null, CredentialRequired())
                : (null, null);
    }

    private static string? GetCredentialReference(ConnectionConfiguration configuration) =>
        configuration switch
        {
            SqlServerConnectionConfiguration sql =>
                sql.Authentication.CredentialSecretReference,
            RelationalDatabaseConnectionConfiguration database =>
                database.Authentication.CredentialSecretReference,
            FtpConnectionConfiguration ftp => ftp.CredentialSecretReference,
            _ => null
        };

    private static ConnectionCommandResult CredentialRequired() => new(
        ConnectionSaveStatus.Conflict,
        null,
        "connection.credential.required",
        "Enter a username and password to store the referenced credential.");
}
