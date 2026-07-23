using THub.Application.Alerts;
using THub.Application.Connections;
using THub.Application.Execution;
using THub.Domain.Alerts;
using THub.Domain.Connections;
using THub.Domain.Workflows;
using THub.Infrastructure.Connections;
using THub.Infrastructure.Files;
using Microsoft.Data.SqlClient;

namespace THub.Infrastructure.Execution;

/// <summary>
/// Revalidates mutable connector and Email-profile references at the Worker boundary. Every
/// operation is read-only; executors repeat the checks immediately before using the resource.
/// </summary>
public sealed class InfrastructureWorkflowNodeResourceValidator(
    ExecutionConnectionResolver connectionResolver,
    SqlServerConnectionStringFactory connectionStringFactory,
    RelationalConnectionFactory relationalConnectionFactory,
    FtpClientFactory ftpClientFactory,
    ApprovedPathResolver pathResolver,
    IEmailAlertAdministrationStore emailProfiles) : IWorkflowNodeResourceValidator
{
    private readonly ExecutionConnectionResolver _connectionResolver =
        connectionResolver ?? throw new ArgumentNullException(nameof(connectionResolver));
    private readonly ApprovedPathResolver _pathResolver =
        pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
    private readonly SqlServerConnectionStringFactory _connectionStringFactory =
        connectionStringFactory ?? throw new ArgumentNullException(nameof(connectionStringFactory));
    private readonly IEmailAlertAdministrationStore _emailProfiles =
        emailProfiles ?? throw new ArgumentNullException(nameof(emailProfiles));

    public async ValueTask ValidateAsync(
        WorkflowNode node,
        WorkflowNodeSettings settings,
        TabularExecutionLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(limits);

        switch (settings)
        {
            case SqlSourceNodeSettings sqlSource:
                await ValidateDatabaseSourceAsync(node.Kind, sqlSource, cancellationToken).ConfigureAwait(false);
                break;
            case SqlTargetNodeSettings sqlTarget:
                await ValidateDatabaseTargetAsync(node.Kind, sqlTarget, cancellationToken).ConfigureAwait(false);
                break;
            case FtpSourceNodeSettings ftpSource:
                await ValidateFtpSourceAsync(ftpSource, cancellationToken).ConfigureAwait(false);
                break;
            case FtpTargetNodeSettings ftpTarget:
                await ValidateFtpTargetAsync(ftpTarget, cancellationToken).ConfigureAwait(false);
                break;
            case CsvSourceNodeSettings csvSource:
                await ValidateCsvSourceAsync(csvSource, limits, cancellationToken).ConfigureAwait(false);
                break;
            case ExcelSourceNodeSettings excelSource:
                await ValidateExcelSourceAsync(excelSource, limits, cancellationToken).ConfigureAwait(false);
                break;
            case CsvTargetNodeSettings csvTarget:
                await ValidateFileTargetAsync(
                    csvTarget.ConnectionId,
                    ConnectionKind.CsvFile,
                    csvTarget.RelativePath,
                    cancellationToken).ConfigureAwait(false);
                break;
            case ExcelTargetNodeSettings excelTarget:
                await ValidateFileTargetAsync(
                    excelTarget.ConnectionId,
                    ConnectionKind.ExcelFile,
                    excelTarget.RelativePath,
                    cancellationToken).ConfigureAwait(false);
                break;
            case EmailAlertNodeSettings email:
                await ValidateEmailAsync(email, cancellationToken).ConfigureAwait(false);
                break;
            case SelectColumnsNodeSettings or FilterRowsNodeSettings or JoinNodeSettings:
                break;
            default:
                throw ExecutionFailure.Configuration(
                    "execution.preflight.settings.unsupported",
                    $"Preflight does not support settings for node kind '{node.Kind}'.");
        }
    }

    private async Task ValidateDatabaseSourceAsync(
        WorkflowNodeKind nodeKind,
        SqlSourceNodeSettings settings,
        CancellationToken cancellationToken)
    {
        if (nodeKind != WorkflowNodeKind.SqlSource)
        {
            await ValidateRelationalAsync(nodeKind, settings.Schema, settings.Object, settings.ConnectionId, cancellationToken);
            return;
        }
        var connection = await _connectionResolver.ResolveAsync<SqlServerConnectionConfiguration>(
            settings.ConnectionId,
            ConnectionKind.SqlServer,
            cancellationToken).ConfigureAwait(false);
        var metadata = await SqlExecutionSupport.LoadObjectMetadataAsync(
            (await _connectionStringFactory.CreateAsync(
                connection,
                "THub workflow preflight",
                ApplicationIntent.ReadWrite,
                enlist: true,
                cancellationToken).ConfigureAwait(false)).ConnectionString,
            connection.CommandTimeoutSeconds,
            settings.Schema,
            settings.Object,
            allowView: true,
            cancellationToken).ConfigureAwait(false);
        _ = SqlExecutionSupport.SelectColumns(metadata, settings.Columns);
    }

    private async Task ValidateDatabaseTargetAsync(
        WorkflowNodeKind nodeKind,
        SqlTargetNodeSettings settings,
        CancellationToken cancellationToken)
    {
        if (nodeKind != WorkflowNodeKind.SqlTarget)
        {
            await ValidateRelationalTargetAsync(
                nodeKind,
                settings,
                cancellationToken).ConfigureAwait(false);
            return;
        }
        var connection = await _connectionResolver.ResolveAsync<SqlServerConnectionConfiguration>(
            settings.ConnectionId,
            ConnectionKind.SqlServer,
            cancellationToken).ConfigureAwait(false);
        var metadata = await SqlExecutionSupport.LoadObjectMetadataAsync(
            (await _connectionStringFactory.CreateAsync(
                connection,
                "THub workflow preflight",
                ApplicationIntent.ReadWrite,
                enlist: true,
                cancellationToken).ConfigureAwait(false)).ConnectionString,
            connection.CommandTimeoutSeconds,
            settings.Schema,
            settings.Object,
            allowView: false,
            cancellationToken).ConfigureAwait(false);
        ValidateConfiguredTargetColumns(metadata, settings);
    }

    private async Task ValidateRelationalTargetAsync(
        WorkflowNodeKind nodeKind,
        SqlTargetNodeSettings settings,
        CancellationToken cancellationToken)
    {
        var connectionKind = ExpectedRelationalKind(nodeKind);
        var configuration =
            await _connectionResolver.ResolveAsync<RelationalDatabaseConnectionConfiguration>(
                settings.ConnectionId,
                connectionKind,
                cancellationToken).ConfigureAwait(false);
        await using var connection = await relationalConnectionFactory.CreateAsync(
            configuration,
            cancellationToken);
        var metadata = await RelationalExecutionSupport.LoadMetadataAsync(
            connection,
            configuration,
            settings.Schema,
            settings.Object,
            cancellationToken).ConfigureAwait(false);
        ValidateConfiguredTargetColumns(
            settings,
            metadata.Select(column => new TargetColumnPolicy(
                column.Name,
                column.CanWrite,
                column.IsNullable,
                HasDefault: false,
                column.IsKey)).ToArray(),
            "execution.database.target");
    }

    private async Task ValidateRelationalAsync(
        WorkflowNodeKind nodeKind,
        string schema,
        string objectName,
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        var connectionKind = ExpectedRelationalKind(nodeKind);
        var configuration = await _connectionResolver.ResolveAsync<RelationalDatabaseConnectionConfiguration>(
            connectionId,
            connectionKind,
            cancellationToken);
        await using var connection = await relationalConnectionFactory.CreateAsync(configuration, cancellationToken);
        _ = await RelationalExecutionSupport.LoadMetadataAsync(
            connection,
            configuration,
            schema,
            objectName,
            cancellationToken);
    }

    private static ConnectionKind ExpectedRelationalKind(WorkflowNodeKind nodeKind) =>
        nodeKind switch
        {
            WorkflowNodeKind.MySqlSource or WorkflowNodeKind.MySqlTarget => ConnectionKind.MySql,
            WorkflowNodeKind.PostgreSqlSource or WorkflowNodeKind.PostgreSqlTarget => ConnectionKind.PostgreSql,
            WorkflowNodeKind.OracleSource or WorkflowNodeKind.OracleTarget => ConnectionKind.Oracle,
            _ => throw new ArgumentOutOfRangeException(nameof(nodeKind))
        };

    private async Task ValidateFtpSourceAsync(
        FtpSourceNodeSettings settings,
        CancellationToken cancellationToken)
    {
        var configuration = await _connectionResolver.ResolveAsync<FtpConnectionConfiguration>(
            settings.ConnectionId,
            ConnectionKind.Ftp,
            cancellationToken);
        await using var client = await ftpClientFactory.CreateConnectedAsync(configuration, cancellationToken);
        var size = await client.GetFileSize(settings.RemotePath, -1, cancellationToken);
        if (size < 0 || size > configuration.MaximumFileBytes)
        {
            throw ExecutionFailure.Configuration(
                "execution.ftp.source.invalid",
                "The FTP source is unavailable or exceeds its configured byte limit.");
        }
    }

    private async Task ValidateFtpTargetAsync(
        FtpTargetNodeSettings settings,
        CancellationToken cancellationToken)
    {
        var configuration = await _connectionResolver.ResolveAsync<FtpConnectionConfiguration>(
            settings.ConnectionId,
            ConnectionKind.Ftp,
            cancellationToken);
        await using var client = await ftpClientFactory.CreateConnectedAsync(configuration, cancellationToken);
        if (await client.FileExists(settings.RemotePath, cancellationToken))
        {
            throw ExecutionFailure.Configuration(
                "execution.ftp.target.exists",
                "The FTP target already exists and createNew mode cannot overwrite it.");
        }
    }

    private async Task ValidateCsvSourceAsync(
        CsvSourceNodeSettings settings,
        TabularExecutionLimits limits,
        CancellationToken cancellationToken)
    {
        var connection = await _connectionResolver.ResolveAsync<FileConnectionConfiguration>(
            settings.ConnectionId,
            ConnectionKind.CsvFile,
            cancellationToken).ConfigureAwait(false);
        var path = ResolveFile(connection, settings.RelativePath);
        _ = CsvSourceNodeExecutor.RequireBoundedSourceFile(path, connection.MaximumFileBytes);
        _ = await CsvSourceNodeExecutor.DiscoverCsvSchemaAsync(
            path,
            settings,
            connection,
            limits,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateExcelSourceAsync(
        ExcelSourceNodeSettings settings,
        TabularExecutionLimits limits,
        CancellationToken cancellationToken)
    {
        var connection = await _connectionResolver.ResolveAsync<FileConnectionConfiguration>(
            settings.ConnectionId,
            ConnectionKind.ExcelFile,
            cancellationToken).ConfigureAwait(false);
        var path = ResolveFile(connection, settings.RelativePath);
        _ = CsvSourceNodeExecutor.RequireBoundedSourceFile(path, connection.MaximumFileBytes);
        _ = ExcelSourceNodeExecutor.DiscoverSchema(
            path,
            settings,
            connection,
            limits,
            cancellationToken);
    }

    private async Task ValidateFileTargetAsync(
        Guid connectionId,
        ConnectionKind connectionKind,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var connection = await _connectionResolver.ResolveAsync<FileConnectionConfiguration>(
            connectionId,
            connectionKind,
            cancellationToken).ConfigureAwait(false);
        FileTargetSupport.EnsureNewTarget(ResolveFile(connection, relativePath));
    }

    private async Task ValidateEmailAsync(
        EmailAlertNodeSettings settings,
        CancellationToken cancellationToken)
    {
        var profile = await _emailProfiles.FindProfileAsync(settings.ProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            throw ExecutionFailure.Configuration(
                "execution.email.profile.not_found",
                "The configured Email delivery profile was not found.");
        }

        if (!profile.IsEnabled)
        {
            throw ExecutionFailure.Configuration(
                "execution.email.profile.disabled",
                "The configured Email delivery profile is disabled.");
        }

        try
        {
            var message = new EmailTemplate(settings.Subject, settings.Body).Render(
                settings.Recipients,
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["run.id"] = Guid.Empty.ToString("D")
                });
            profile.ValidateMessage(message);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw ExecutionFailure.Configuration(
                "execution.email.policy",
                "The Email action does not satisfy its delivery profile policy.",
                exception);
        }
    }

    private string ResolveFile(FileConnectionConfiguration connection, string relativePath)
    {
        try
        {
            return _pathResolver.ResolveFile(
                connection.ApprovedRoot,
                relativePath,
                connection.AllowUncRoot);
        }
        catch (InvalidOperationException exception)
        {
            throw ExecutionFailure.Configuration(
                "execution.file.path.invalid",
                "The configured file path is outside its approved root or uses a disallowed path type.",
                exception);
        }
    }

    private static void ValidateConfiguredTargetColumns(
        IReadOnlyList<SqlColumnMetadata> metadata,
        SqlTargetNodeSettings settings)
    {
        ValidateConfiguredTargetColumns(
            settings,
            metadata.Select(column => new TargetColumnPolicy(
                column.Name,
                column.CanWrite,
                column.IsNullable,
                column.HasDefault,
                column.IsKey)).ToArray(),
            "execution.sql.target");
    }

    private static void ValidateConfiguredTargetColumns(
        SqlTargetNodeSettings settings,
        IReadOnlyList<TargetColumnPolicy> metadata,
        string errorPrefix)
    {
        var primaryKey = metadata
            .Where(column => column.IsKey)
            .Select(column => column.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (settings.Mode is "upsert" or "delete"
            && (primaryKey.Count == 0
                || !primaryKey.SetEquals(settings.KeyColumns)))
        {
            throw ExecutionFailure.Configuration(
                $"{errorPrefix}.keys",
                "Configured key columns must exactly match the discovered primary key.");
        }

        if (settings.Bindings.Count == 0)
        {
            return;
        }

        var targetByName = metadata.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        var mappedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in settings.Bindings)
        {
            var targetName = binding.TargetColumn;
            if (!targetByName.TryGetValue(targetName, out var target))
            {
                throw ExecutionFailure.Configuration(
                    $"{errorPrefix}.column",
                    $"Configured target column '{targetName}' does not exist.");
            }

            var isDeleteKey = settings.Mode == "delete" && target.IsKey;
            if (!target.CanWrite && !isDeleteKey)
            {
                throw ExecutionFailure.Configuration(
                    $"{errorPrefix}.generated",
                    $"Configured target column '{target.Name}' is identity, computed, or generated.");
            }

            if (!mappedTargets.Add(target.Name))
            {
                throw ExecutionFailure.Configuration(
                    $"{errorPrefix}.duplicate",
                    $"Target column '{target.Name}' is mapped more than once.");
            }
        }

        if (settings.Mode is "insert" or "upsert"
            && metadata.Any(column => column.CanWrite
                    && !column.IsNullable
                    && !column.HasDefault
                    && !mappedTargets.Contains(column.Name)))
        {
            throw ExecutionFailure.Configuration(
                $"{errorPrefix}.required",
                "One or more required SQL target columns are not mapped.");
        }
    }

    private sealed record TargetColumnPolicy(
        string Name,
        bool CanWrite,
        bool IsNullable,
        bool HasDefault,
        bool IsKey);
}
