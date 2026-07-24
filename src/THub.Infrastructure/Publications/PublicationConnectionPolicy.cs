using THub.Application.Connections;
using THub.Application.Publications;
using THub.Domain.Connections;

namespace THub.Infrastructure.Publications;

public sealed class PublicationConnectionPolicy(
    IDataConnectionStore connectionStore,
    ConnectionConfigurationSerializer serializer)
    : IPublicationConnectionPolicy
{
    public async Task<PublicationConnectionPolicyResult> ValidateAsync(
        Guid readConnectionId,
        Guid? applyConnectionId,
        bool requiresApplyConnection,
        CancellationToken cancellationToken)
    {
        if (readConnectionId == Guid.Empty)
        {
            return Failure("publication.read_connection_required", "A read connection is required.");
        }

        var read = await connectionStore.FindAsync(readConnectionId, cancellationToken)
            .ConfigureAwait(false);
        if (read is null || !read.IsEnabled || !IsRelational(read.Kind))
        {
            return Failure(
                "publication.read_connection_unavailable",
                "The publication read connection is missing, disabled, or unsupported.");
        }

        ConnectionConfiguration readConfiguration;
        try
        {
            readConfiguration = serializer.Deserialize(read);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                InvalidOperationException or
                ConnectionConfigurationException)
        {
            return Failure(
                "publication.read_connection_invalid",
                "The publication read connection configuration is invalid.");
        }

        if (!requiresApplyConnection)
        {
            return applyConnectionId is null
                ? PublicationConnectionPolicyResult.Success
                : Failure(
                    "publication.apply_connection_not_allowed",
                    "A read-only publication version cannot declare an apply connection.");
        }

        if (applyConnectionId is null || applyConnectionId == Guid.Empty)
        {
            return Failure(
                "publication.apply_connection_required",
                "A writable editor version requires a separate Worker apply connection.");
        }

        if (applyConnectionId == readConnectionId)
        {
            return Failure(
                "publication.apply_connection_must_be_separate",
                "The read and Worker apply connections must be different.");
        }

        var apply = await connectionStore.FindAsync(applyConnectionId.Value, cancellationToken)
            .ConfigureAwait(false);
        if (apply is null || !apply.IsEnabled || !IsRelational(apply.Kind))
        {
            return Failure(
                "publication.apply_connection_unavailable",
                "The Worker apply connection is missing, disabled, or unsupported.");
        }

        if (apply.Kind != read.Kind)
        {
            return Failure(
                "publication.connection_provider_mismatch",
                "Read and apply connections must use the same database provider.");
        }

        ConnectionConfiguration applyConfiguration;
        try
        {
            applyConfiguration = serializer.Deserialize(apply);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                InvalidOperationException or
                ConnectionConfigurationException)
        {
            return Failure(
                "publication.apply_connection_invalid",
                "The Worker apply connection configuration is invalid.");
        }

        if (!TargetsSameDatabase(readConfiguration, applyConfiguration))
        {
            return Failure(
                "publication.connection_target_mismatch",
                "Read and apply connections must target the same database endpoint.");
        }

        if (UsesSameStoredCredential(readConfiguration, applyConfiguration))
        {
            return Failure(
                "publication.apply_credential_must_be_separate",
                "The Worker apply connection must use a different stored credential reference.");
        }

        return PublicationConnectionPolicyResult.Success;
    }

    private static bool TargetsSameDatabase(
        ConnectionConfiguration read,
        ConnectionConfiguration apply) =>
        (read, apply) switch
        {
            (SqlServerConnectionConfiguration left, SqlServerConnectionConfiguration right) =>
                string.Equals(left.Server, right.Server, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(left.Database, right.Database, StringComparison.OrdinalIgnoreCase),
            (RelationalDatabaseConnectionConfiguration left,
                RelationalDatabaseConnectionConfiguration right) =>
                left.Kind == right.Kind &&
                left.Port == right.Port &&
                string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(left.Database, right.Database, StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private static bool UsesSameStoredCredential(
        ConnectionConfiguration read,
        ConnectionConfiguration apply)
    {
        var readAuthentication = GetAuthentication(read);
        var applyAuthentication = GetAuthentication(apply);
        return readAuthentication.Kind == DatabaseAuthenticationKind.UserPassword &&
            applyAuthentication.Kind == DatabaseAuthenticationKind.UserPassword &&
            string.Equals(
                readAuthentication.CredentialSecretReference,
                applyAuthentication.CredentialSecretReference,
                StringComparison.Ordinal);
    }

    private static DatabaseAuthenticationConfiguration GetAuthentication(
        ConnectionConfiguration configuration) => configuration switch
    {
        SqlServerConnectionConfiguration sqlServer => sqlServer.Authentication,
        RelationalDatabaseConnectionConfiguration relational => relational.Authentication,
        _ => throw new ArgumentOutOfRangeException(nameof(configuration))
    };

    private static bool IsRelational(ConnectionKind kind) => kind is
        ConnectionKind.SqlServer or
        ConnectionKind.MySql or
        ConnectionKind.PostgreSql or
        ConnectionKind.Oracle;

    private static PublicationConnectionPolicyResult Failure(
        string code,
        string message) =>
        PublicationConnectionPolicyResult.Failure(code, message);
}
