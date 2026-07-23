using THub.Application.Connections;
using THub.Application.Execution;
using THub.Domain.Connections;

namespace THub.Application.Workflows;

public sealed record WorkflowSchemaColumnDto(
    string Name,
    string SourceTypeName,
    TabularDataType DataType,
    bool IsNullable,
    bool IsWritable);

public sealed record WorkflowObjectSchemaDto(
    Guid ConnectionId,
    string Schema,
    string ObjectName,
    IReadOnlyList<WorkflowSchemaColumnDto> Columns);

public enum WorkflowSchemaInspectionStatus
{
    Success,
    NotFound,
    Invalid,
    Unavailable
}

public sealed record WorkflowSchemaInspectionResult(
    WorkflowSchemaInspectionStatus Status,
    WorkflowObjectSchemaDto? Schema,
    string Message)
{
    public bool IsSuccess => Status == WorkflowSchemaInspectionStatus.Success && Schema is not null;
}

public interface IWorkflowSchemaInspector
{
    Task<WorkflowSchemaInspectionResult> InspectAsync(
        DataConnection connection,
        string schema,
        string objectName,
        bool requireWritableTable,
        CancellationToken cancellationToken);
}

public sealed class WorkflowSchemaInspectionService(
    IDataConnectionStore connectionStore,
    IWorkflowSchemaInspector inspector)
{
    public async Task<WorkflowSchemaInspectionResult> InspectAsync(
        Guid connectionId,
        ConnectionKind expectedKind,
        string schema,
        string objectName,
        bool requireWritableTable,
        CancellationToken cancellationToken)
    {
        if (connectionId == Guid.Empty ||
            !IsIdentifier(schema) ||
            !IsIdentifier(objectName) ||
            !IsRelational(expectedKind))
        {
            return new(
                WorkflowSchemaInspectionStatus.Invalid,
                null,
                "Select a relational connection and provide bounded schema and object names.");
        }

        var connection = await connectionStore.FindAsync(connectionId, cancellationToken)
            .ConfigureAwait(false);
        if (connection is null)
        {
            return new(
                WorkflowSchemaInspectionStatus.NotFound,
                null,
                "The selected connection no longer exists.");
        }

        if (!connection.IsEnabled || connection.Kind != expectedKind)
        {
            return new(
                WorkflowSchemaInspectionStatus.Invalid,
                null,
                $"Schema inspection requires an enabled {expectedKind} connection.");
        }

        return await inspector.InspectAsync(
                connection,
                schema.Trim(),
                objectName.Trim(),
                requireWritableTable,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        value.All(character => !char.IsControl(character));

    private static bool IsRelational(ConnectionKind kind) => kind is
        ConnectionKind.SqlServer or ConnectionKind.MySql
            or ConnectionKind.PostgreSql or ConnectionKind.Oracle;
}
