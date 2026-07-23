using THub.Domain.Publications;

namespace THub.Application.Publications;

public enum PublicationFilterOperator
{
    Equal,
    NotEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    StartsWith,
    Contains,
    IsNull,
    IsNotNull,
}

public sealed record PublicationFilter(
    string ColumnAlias,
    PublicationFilterOperator Operator,
    string? Value);

public sealed record PublicationSort(string ColumnAlias, bool Descending = false);

public sealed record PublicationRestRowsQuery(
    int? PageSize = null,
    string? Cursor = null,
    IReadOnlyList<PublicationFilter>? Filters = null,
    IReadOnlyList<PublicationSort>? Sorts = null);

public sealed record PublicationEditorRowsQuery(
    int? WindowSize = null,
    string? Cursor = null,
    IReadOnlyList<PublicationFilter>? Filters = null,
    IReadOnlyList<PublicationSort>? Sorts = null);

public sealed record PublicationRowDto(IReadOnlyDictionary<string, object?> Values);

public sealed record PublicationRowPageDto(
    IReadOnlyList<PublicationRowDto> Rows,
    string? NextCursor);

public sealed record PublicationSourceReadQuery(
    int Take,
    string? Cursor,
    IReadOnlyList<PublicationFilter> Filters,
    IReadOnlyList<PublicationSort> Sorts);

public sealed record PublicationSourceRowPage(
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    string? NextCursor);

public sealed record PublicationForeignKeyLookupQuery(
    Guid PublicationId,
    string ColumnAlias,
    IReadOnlyCollection<Guid> RoleIds,
    string? Search = null,
    int Take = 50,
    string? Cursor = null);

public sealed record PublicationForeignKeySourceQuery(
    int Take,
    string? Cursor,
    string? Search);

public sealed record PublicationLookupItemDto(
    IReadOnlyDictionary<string, object?> KeyValues,
    string DisplayText);

public sealed record PublicationLookupPageDto(
    IReadOnlyList<PublicationLookupItemDto> Items,
    string? NextCursor);

public sealed record PublicationSourceLookupPage(
    IReadOnlyList<PublicationLookupItemDto> Items,
    string? NextCursor);

public sealed record PublicationForeignKeyLabelRequest(
    int RequestId,
    string ColumnAlias,
    IReadOnlyDictionary<string, object?> KeyValues);

public sealed record PublicationForeignKeyLabelQuery(
    Guid PublicationId,
    IReadOnlyCollection<Guid> RoleIds,
    IReadOnlyList<PublicationForeignKeyLabelRequest> Requests);

public sealed record PublicationForeignKeyLabelDto(
    int RequestId,
    string DisplayText);

public sealed record PublicationForeignKeyLabelResult(
    IReadOnlyList<PublicationForeignKeyLabelDto> Labels);

public sealed record PublicationForeignKeyTuple(
    int RequestId,
    string ConstraintName,
    IReadOnlyDictionary<string, object?> KeyValues);

public sealed record PublicationSourceForeignKeyResolution(
    IReadOnlyList<PublicationForeignKeyLabelDto> Labels);

public enum PublicationSourceReadStatus
{
    Success,
    InvalidCursor,
    SchemaChanged,
    Unavailable,
}

public sealed record PublicationSourceReadResult<T>(
    PublicationSourceReadStatus Status,
    T? Value)
    where T : class;

public interface IPublicationSourceDataReader
{
    Task<PublicationSourceReadResult<PublicationSourceRowPage>> ReadRowsAsync(
        PublicationVersion version,
        PublicationSourceReadQuery query,
        CancellationToken cancellationToken);

    Task<PublicationSourceReadResult<PublicationSourceLookupPage>> ReadForeignKeyLookupAsync(
        PublicationVersion version,
        PublicationColumn column,
        PublicationForeignKeySourceQuery query,
        CancellationToken cancellationToken);

    Task<PublicationSourceReadResult<PublicationSourceForeignKeyResolution>> ResolveForeignKeysAsync(
        PublicationVersion version,
        IReadOnlyList<PublicationForeignKeyTuple> tuples,
        CancellationToken cancellationToken);
}
