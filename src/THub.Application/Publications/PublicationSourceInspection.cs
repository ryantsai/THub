using THub.Application.Connections;
using THub.Domain.Connections;
using THub.Domain.Publications;

namespace THub.Application.Publications;

public sealed record PublicationSourceObjectDto(
    string Schema,
    string Name,
    PublicationSourceObjectKind Kind,
    int ColumnCount);

public sealed record PublicationSourceObjectPageDto(
    IReadOnlyList<PublicationSourceObjectDto> Objects,
    bool HasMore);

public sealed record PublicationSourceColumnDto(
    int Ordinal,
    string Name,
    string SourceTypeName,
    PublicationDataType? DataType,
    bool IsSupported,
    bool IsNullable,
    bool IsGenerated,
    bool IsRowVersion,
    bool IsKey,
    int? KeyOrdinal,
    int? MaximumLength,
    byte? NumericPrecision,
    byte? NumericScale);

public sealed record PublicationSourceForeignKeyColumnDto(
    int Ordinal,
    string LocalColumn,
    string ReferencedColumn);

public sealed record PublicationSourceForeignKeyDto(
    string ConstraintName,
    string ReferencedSchema,
    string ReferencedObject,
    IReadOnlyList<PublicationSourceForeignKeyColumnDto> Columns,
    IReadOnlyList<string> DisplayCandidates,
    IReadOnlyList<string> SearchCandidates,
    string RecommendedDisplayColumn,
    PublicationLookupMode RecommendedLookupMode);

public sealed record PublicationSourceObjectInspectionDto(
    Guid ConnectionId,
    string Schema,
    string Name,
    PublicationSourceObjectKind Kind,
    string SchemaFingerprint,
    IReadOnlyList<PublicationSourceColumnDto> Columns,
    IReadOnlyList<PublicationSourceForeignKeyDto> ForeignKeys);

public enum PublicationSourceInspectionStatus
{
    Success,
    NotFound,
    Unsupported,
    BoundsExceeded,
    Unavailable,
}

public sealed record PublicationSourceInspectionResult<T>(
    PublicationSourceInspectionStatus Status,
    T? Value)
    where T : class;

public interface IPublicationSourceSchemaInspector
{
    Task<PublicationSourceInspectionResult<PublicationSourceObjectPageDto>> ListObjectsAsync(
        DataConnection connection,
        string? search,
        int take,
        CancellationToken cancellationToken);

    Task<PublicationSourceInspectionResult<PublicationSourceObjectInspectionDto>> InspectObjectAsync(
        DataConnection connection,
        string schema,
        string objectName,
        CancellationToken cancellationToken);
}

public sealed class PublicationSourceInspectionService(
    IDataConnectionStore connectionStore,
    IPublicationSourceSchemaInspector inspector)
{
    private const int MaximumObjectsPerRequest = 200;
    private const int MaximumSearchLength = 256;
    private readonly IDataConnectionStore _connectionStore =
        connectionStore ?? throw new ArgumentNullException(nameof(connectionStore));
    private readonly IPublicationSourceSchemaInspector _inspector =
        inspector ?? throw new ArgumentNullException(nameof(inspector));

    public async Task<PublicationResult<PublicationSourceObjectPageDto>> ListObjectsAsync(
        Guid connectionId,
        string? search,
        int take,
        CancellationToken cancellationToken)
    {
        if (connectionId == Guid.Empty ||
            take is < 1 or > MaximumObjectsPerRequest ||
            search?.Length > MaximumSearchLength)
        {
            return PublicationResultFactory.Validation<PublicationSourceObjectPageDto>(
                "publication.source_query_invalid",
                $"Connection, search, and take from 1 to {MaximumObjectsPerRequest} must be bounded.");
        }

        var connection = await RequireSqlConnectionAsync(connectionId, cancellationToken)
            .ConfigureAwait(false);
        if (!connection.IsSuccess)
        {
            return CopyFailure<DataConnection, PublicationSourceObjectPageDto>(connection);
        }

        var inspected = await _inspector.ListObjectsAsync(
                connection.Value!,
                string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
                take,
                cancellationToken)
            .ConfigureAwait(false);
        return Map(inspected);
    }

    public async Task<PublicationResult<PublicationSourceObjectInspectionDto>> InspectObjectAsync(
        Guid connectionId,
        string schema,
        string objectName,
        CancellationToken cancellationToken)
    {
        if (connectionId == Guid.Empty ||
            !IsBoundedIdentifier(schema) ||
            !IsBoundedIdentifier(objectName))
        {
            return PublicationResultFactory.Validation<PublicationSourceObjectInspectionDto>(
                "publication.source_object_invalid",
                "Connection, schema, and object identifiers are required and bounded.");
        }

        var connection = await RequireSqlConnectionAsync(connectionId, cancellationToken)
            .ConfigureAwait(false);
        if (!connection.IsSuccess)
        {
            return CopyFailure<DataConnection, PublicationSourceObjectInspectionDto>(connection);
        }

        var inspected = await _inspector.InspectObjectAsync(
                connection.Value!,
                schema.Trim(),
                objectName.Trim(),
                cancellationToken)
            .ConfigureAwait(false);
        return Map(inspected);
    }

    private async Task<PublicationResult<DataConnection>> RequireSqlConnectionAsync(
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        var connection = await _connectionStore.FindAsync(connectionId, cancellationToken)
            .ConfigureAwait(false);
        if (connection is null)
        {
            return PublicationResultFactory.NotFound<DataConnection>(
                "connection.not_found",
                "The source connection was not found.");
        }

        if (!connection.IsEnabled || connection.Kind != ConnectionKind.SqlServer)
        {
            return PublicationResultFactory.Conflict<DataConnection>(
                "publication.source_connection_unavailable",
                "Source inspection requires an enabled SQL Server connection.");
        }

        return PublicationResult<DataConnection>.Success(connection);
    }

    private static PublicationResult<T> Map<T>(PublicationSourceInspectionResult<T> result)
        where T : class =>
        result.Status switch
        {
            PublicationSourceInspectionStatus.Success when result.Value is not null =>
                PublicationResult<T>.Success(result.Value),
            PublicationSourceInspectionStatus.NotFound => PublicationResultFactory.NotFound<T>(
                "publication.source_object_not_found",
                "The source object was not found."),
            PublicationSourceInspectionStatus.Unsupported => PublicationResultFactory.Validation<T>(
                "publication.source_object_unsupported",
                "The source object cannot be represented by the current tabular publication contract."),
            PublicationSourceInspectionStatus.BoundsExceeded => PublicationResultFactory.Validation<T>(
                "publication.source_metadata_too_large",
                "The source metadata exceeds publication inspection bounds."),
            _ => PublicationResultFactory.Unavailable<T>(
                "publication.source_inspection_unavailable",
                "The source schema is temporarily unavailable."),
        };

    private static PublicationResult<TTarget> CopyFailure<TSource, TTarget>(
        PublicationResult<TSource> result)
    {
        var problem = result.Problem ?? throw new InvalidOperationException("A failed result requires a problem.");
        return PublicationResult<TTarget>.Failure(problem.Kind, problem.Code, problem.Message);
    }

    private static bool IsBoundedIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        !value.Any(char.IsControl);
}
