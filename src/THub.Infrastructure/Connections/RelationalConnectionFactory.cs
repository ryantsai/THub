using System.Data.Common;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using THub.Application.Connections;
using THub.Domain.Connections;

namespace THub.Infrastructure.Connections;

public sealed class RelationalConnectionFactory(IConnectionCredentialResolver credentialResolver)
{
    public async ValueTask<DbConnection> CreateAsync(
        RelationalDatabaseConnectionConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var credential = await credentialResolver.ResolveAsync(
                configuration.Authentication.CredentialSecretReference!,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ConnectionCredentialUnavailableException();

        return configuration.Kind switch
        {
            ConnectionKind.MySql => CreateMySql(configuration, credential),
            ConnectionKind.PostgreSql => CreatePostgreSql(configuration, credential),
            ConnectionKind.Oracle => CreateOracle(configuration, credential),
            _ => throw new ArgumentOutOfRangeException(nameof(configuration))
        };
    }

    private static MySqlConnection CreateMySql(
        RelationalDatabaseConnectionConfiguration configuration,
        ConnectionCredential credential)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = configuration.Host,
            Port = checked((uint)configuration.Port),
            Database = configuration.Database,
            UserID = credential.UserName,
            Password = credential.Password,
            ConnectionTimeout = checked((uint)configuration.ConnectTimeoutSeconds),
            SslMode = configuration.Encrypt
                ? configuration.TrustServerCertificate
                    ? MySqlSslMode.Required
                    : MySqlSslMode.VerifyFull
                : MySqlSslMode.Disabled,
            PersistSecurityInfo = false,
            AllowUserVariables = false
        };
        return new MySqlConnection(builder.ConnectionString);
    }

    private static NpgsqlConnection CreatePostgreSql(
        RelationalDatabaseConnectionConfiguration configuration,
        ConnectionCredential credential)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = configuration.Host,
            Port = configuration.Port,
            Database = configuration.Database,
            Username = credential.UserName,
            Password = credential.Password,
            Timeout = configuration.ConnectTimeoutSeconds,
            SslMode = configuration.Encrypt
                ? configuration.TrustServerCertificate ? SslMode.Require : SslMode.VerifyFull
                : SslMode.Disable,
            PersistSecurityInfo = false,
            Enlist = false,
            ApplicationName = "THub"
        };
        return new NpgsqlConnection(builder.ConnectionString);
    }

    private static OracleConnection CreateOracle(
        RelationalDatabaseConnectionConfiguration configuration,
        ConnectionCredential credential)
    {
        var protocol = configuration.Encrypt ? "TCPS" : "TCP";
        var security = configuration.Encrypt
            ? $"(SECURITY=(SSL_SERVER_DN_MATCH={(configuration.TrustServerCertificate ? "NO" : "YES")}))"
            : string.Empty;
        var dataSource =
            $"(DESCRIPTION=(ADDRESS=(PROTOCOL={protocol})(HOST={configuration.Host})(PORT={configuration.Port}))(CONNECT_DATA=(SERVICE_NAME={configuration.Database})){security})";
        var builder = new OracleConnectionStringBuilder
        {
            DataSource = dataSource,
            UserID = credential.UserName,
            Password = credential.Password,
            ConnectionTimeout = configuration.ConnectTimeoutSeconds,
            PersistSecurityInfo = false
        };
        return new OracleConnection(builder.ConnectionString);
    }
}
