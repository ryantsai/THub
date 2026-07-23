using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using THub.Application.Connections;
using THub.Application.Execution;
using THub.Application.Workflows;
using THub.Domain.Connections;
using THub.Infrastructure.Connections;
using THub.Infrastructure.Execution;

namespace THub.Infrastructure.Workflows;

public sealed class InfrastructureWorkflowSchemaInspector(
    ConnectionConfigurationSerializer serializer,
    SqlServerConnectionStringFactory sqlServerConnectionFactory,
    RelationalConnectionFactory relationalConnectionFactory,
    ILogger<InfrastructureWorkflowSchemaInspector> logger)
    : IWorkflowSchemaInspector
{
    public async Task<WorkflowSchemaInspectionResult> InspectAsync(
        DataConnection connection,
        string schema,
        string objectName,
        bool requireWritableTable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        try
        {
            return connection.Kind == ConnectionKind.SqlServer
                ? await InspectSqlServerAsync(
                    connection,
                    schema,
                    objectName,
                    requireWritableTable,
                    cancellationToken).ConfigureAwait(false)
                : await InspectRelationalAsync(
                    connection,
                    schema,
                    objectName,
                    cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            logger.LogWarning(
                exception,
                "Workflow schema inspection failed for connection {ConnectionId} and object {Schema}.{Object}.",
                connection.Id,
                schema,
                objectName);
            return new(
                WorkflowSchemaInspectionStatus.Unavailable,
                null,
                "The database schema could not be loaded. Verify the connection, credential, object, and metadata permissions.");
        }
    }

    private async Task<WorkflowSchemaInspectionResult> InspectSqlServerAsync(
        DataConnection connection,
        string schema,
        string objectName,
        bool requireWritableTable,
        CancellationToken cancellationToken)
    {
        var configuration = (SqlServerConnectionConfiguration)serializer.Deserialize(connection);
        var builder = await sqlServerConnectionFactory.CreateAsync(
            configuration,
            "THub workflow schema designer",
            ApplicationIntent.ReadOnly,
            enlist: false,
            cancellationToken).ConfigureAwait(false);
        var columns = await SqlExecutionSupport.LoadObjectMetadataAsync(
            builder.ConnectionString,
            configuration.CommandTimeoutSeconds,
            schema,
            objectName,
            allowView: !requireWritableTable,
            cancellationToken).ConfigureAwait(false);
        return Success(
            connection.Id,
            schema,
            objectName,
            columns.Select(column => new WorkflowSchemaColumnDto(
                column.Name,
                column.SqlTypeName,
                column.DataType,
                column.IsNullable,
                column.CanWrite)).ToArray());
    }

    private async Task<WorkflowSchemaInspectionResult> InspectRelationalAsync(
        DataConnection connection,
        string schema,
        string objectName,
        CancellationToken cancellationToken)
    {
        var configuration = (RelationalDatabaseConnectionConfiguration)serializer.Deserialize(connection);
        await using var database = await relationalConnectionFactory.CreateAsync(
            configuration,
            cancellationToken).ConfigureAwait(false);
        var columns = await RelationalExecutionSupport.LoadMetadataAsync(
            database,
            configuration,
            schema,
            objectName,
            cancellationToken).ConfigureAwait(false);
        return Success(
            connection.Id,
            schema,
            objectName,
            columns.Select(column => new WorkflowSchemaColumnDto(
                column.Name,
                column.SourceTypeName,
                column.DataType,
                column.IsNullable,
                column.CanWrite)).ToArray());
    }

    private static WorkflowSchemaInspectionResult Success(
        Guid connectionId,
        string schema,
        string objectName,
        IReadOnlyList<WorkflowSchemaColumnDto> columns) =>
        new(
            WorkflowSchemaInspectionStatus.Success,
            new WorkflowObjectSchemaDto(connectionId, schema, objectName, columns),
            $"Loaded {columns.Count} columns.");

    private static bool IsExpectedFailure(Exception exception) => exception is
        ConnectionConfigurationException
        or ConnectionCredentialUnavailableException
        or WorkflowNodeExecutionException
        or InvalidOperationException
        or System.Data.Common.DbException;
}
