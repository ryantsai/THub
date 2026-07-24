namespace THub.Publications;

public sealed record PublicationRowsResponse(
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Data,
    string? NextCursor,
    Guid Version);

public sealed record PublicationSchemaResponse(
    Guid Version,
    IReadOnlyList<PublicationSchemaColumnResponse> Columns,
    PublicationPagingResponse Paging,
    PublicationFilterContractResponse Filters);

public sealed record PublicationSchemaColumnResponse(
    string Name,
    string Type,
    bool Nullable,
    bool Filterable,
    bool Sortable,
    bool Key);

public sealed record PublicationPagingResponse(
    int DefaultPageSize,
    int MaximumPageSize,
    string Mode);

public sealed record PublicationFilterContractResponse(
    string Syntax,
    string NullSyntax,
    PublicationFilterOperatorsResponse Operators);

public sealed record PublicationFilterOperatorsResponse(
    IReadOnlyList<string> AllTypes,
    IReadOnlyList<string> StringOnly);
