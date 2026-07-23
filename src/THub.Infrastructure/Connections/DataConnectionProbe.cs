using System.Diagnostics;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using THub.Application.Connections;
using THub.Domain.Connections;
using THub.Infrastructure.Files;

namespace THub.Infrastructure.Connections;

public sealed class DataConnectionProbe(
    ConnectionConfigurationSerializer serializer,
    ApprovedPathResolver pathResolver,
    SqlServerConnectionStringFactory connectionStringFactory,
    RelationalConnectionFactory relationalConnectionFactory,
    FtpClientFactory ftpClientFactory,
    ILogger<DataConnectionProbe> logger) : IDataConnectionProbe
{
    public async Task<ConnectionProbeResult> ProbeAsync(
        DataConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var configuration = serializer.Deserialize(connection);
            return configuration switch
            {
                SqlServerConnectionConfiguration sql =>
                    await ProbeSqlAsync(sql, stopwatch, connectionStringFactory, cancellationToken),
                RelationalDatabaseConnectionConfiguration database =>
                    await ProbeDatabaseAsync(database, stopwatch, relationalConnectionFactory, cancellationToken),
                FtpConnectionConfiguration ftp =>
                    await ProbeFtpAsync(ftp, stopwatch, ftpClientFactory, cancellationToken),
                FileConnectionConfiguration file => ProbeFile(file, stopwatch),
                _ => new ConnectionProbeResult(
                    false,
                    stopwatch.Elapsed,
                    "The connector kind is not supported.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is DbException
            or TimeoutException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ConnectionCredentialUnavailableException
            or ConnectionConfigurationException)
        {
            logger.LogWarning(
                exception,
                "Connection probe failed for {ConnectionId} ({ConnectionKind}).",
                connection.Id,
                connection.Kind);
            return new ConnectionProbeResult(
                false,
                stopwatch.Elapsed,
                "The endpoint could not be reached with the configured service identity and policy.");
        }
    }

    private static async Task<ConnectionProbeResult> ProbeDatabaseAsync(
        RelationalDatabaseConnectionConfiguration configuration,
        Stopwatch stopwatch,
        RelationalConnectionFactory connectionFactory,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.CreateAsync(
            configuration,
            cancellationToken);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = configuration.Kind switch
        {
            ConnectionKind.MySql => "SELECT VERSION();",
            ConnectionKind.PostgreSql => "SHOW server_version;",
            ConnectionKind.Oracle => "SELECT banner FROM v$version FETCH FIRST 1 ROWS ONLY",
            _ => throw new ArgumentOutOfRangeException(nameof(configuration))
        };
        command.CommandTimeout = configuration.CommandTimeoutSeconds;
        var version = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
        return new ConnectionProbeResult(
            true,
            stopwatch.Elapsed,
            $"{configuration.Kind} accepted the configured database identity.",
            version);
    }

    private static async Task<ConnectionProbeResult> ProbeFtpAsync(
        FtpConnectionConfiguration configuration,
        Stopwatch stopwatch,
        FtpClientFactory clientFactory,
        CancellationToken cancellationToken)
    {
        await using var client = await clientFactory.CreateConnectedAsync(
            configuration,
            cancellationToken);
        return new ConnectionProbeResult(
            true,
            stopwatch.Elapsed,
            configuration.EncryptionMode == FtpEncryptionMode.None
                ? "The FTP server accepted the credential over an unencrypted connection."
                : "The FTPS server accepted the configured credential.");
    }

    private static async Task<ConnectionProbeResult> ProbeSqlAsync(
        SqlServerConnectionConfiguration configuration,
        Stopwatch stopwatch,
        SqlServerConnectionStringFactory connectionStringFactory,
        CancellationToken cancellationToken)
    {
        var builder = await connectionStringFactory.CreateAsync(
            configuration,
            "THub connection probe",
            ApplicationIntent.ReadWrite,
            enlist: false,
            cancellationToken);

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion'));";
        command.CommandTimeout = configuration.CommandTimeoutSeconds;
        var version = (string?)await command.ExecuteScalarAsync(cancellationToken);
        return new ConnectionProbeResult(
            true,
            stopwatch.Elapsed,
            "SQL Server accepted the configured database identity.",
            version);
    }

    private ConnectionProbeResult ProbeFile(
        FileConnectionConfiguration configuration,
        Stopwatch stopwatch)
    {
        _ = pathResolver.ResolveFile(
            configuration.ApprovedRoot,
            ".",
            configuration.AllowUncRoot);
        return new ConnectionProbeResult(
            true,
            stopwatch.Elapsed,
            "The approved root is available to the service identity.");
    }
}
