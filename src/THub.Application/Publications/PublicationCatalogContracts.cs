using THub.Domain.Publications;

namespace THub.Application.Publications;

public sealed record PublicationCatalogQuery(
    PublicationKind? Kind = null,
    PublicationState? State = null);

public sealed record CreatePublicationCommand(
    string Slug,
    string Name,
    PublicationKind Kind,
    string Actor);

public sealed record CreatePublicationForeignKeyCommand(
    string ConstraintName,
    int Ordinal,
    int ColumnCount,
    string ReferencedSchema,
    string ReferencedObject,
    string ReferencedColumn,
    string DisplayColumn,
    IReadOnlyList<string> SearchColumns,
    PublicationLookupMode LookupMode);

public sealed record CreatePublicationColumnCommand(
    int Ordinal,
    string SourceName,
    string PublicAlias,
    PublicationDataType DataType,
    string SourceTypeName,
    bool IsNullable,
    bool IsReadable,
    bool IsFilterable,
    bool IsSortable,
    bool IsWritable,
    bool IsKey = false,
    int? KeyOrdinal = null,
    bool IsConcurrencyToken = false,
    bool IsGenerated = false,
    int? MaximumLength = null,
    byte? NumericPrecision = null,
    byte? NumericScale = null,
    CreatePublicationForeignKeyCommand? ForeignKey = null);

public sealed record CreatePublicationVersionCommand(
    Guid PublicationId,
    Guid ConnectionId,
    Guid? ApplyConnectionId,
    string SourceSchema,
    string SourceObject,
    PublicationSourceObjectKind SourceObjectKind,
    string SchemaFingerprint,
    PublicationConcurrencyMode ConcurrencyMode,
    PublicationVersionSettings Settings,
    IReadOnlyList<CreatePublicationColumnCommand> Columns,
    string Actor);

public sealed record PublicationSummaryDto(
    Guid Id,
    string Slug,
    string Name,
    PublicationKind Kind,
    PublicationState State,
    Guid? ActiveVersionId,
    DateTimeOffset UpdatedAtUtc);

public sealed record PublicationDetailDto(
    Guid Id,
    string Slug,
    string Name,
    PublicationKind Kind,
    PublicationState State,
    Guid? ActiveVersionId,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc,
    string UpdatedBy,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<PublicationVersionSummaryDto> Versions);

public sealed record PublicationVersionSummaryDto(
    Guid Id,
    int VersionNumber,
    Guid ConnectionId,
    Guid? ApplyConnectionId,
    string SourceSchema,
    string SourceObject,
    PublicationConcurrencyMode ConcurrencyMode,
    DateTimeOffset CreatedAtUtc);

public sealed record PublicationVersionDto(
    Guid Id,
    Guid PublicationId,
    int VersionNumber,
    Guid ConnectionId,
    Guid? ApplyConnectionId,
    string SourceSchema,
    string SourceObject,
    PublicationSourceObjectKind SourceObjectKind,
    string SchemaFingerprint,
    PublicationConcurrencyMode ConcurrencyMode,
    PublicationVersionSettings Settings,
    IReadOnlyList<PublicationColumnDto> Columns,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc);

public sealed record PublicationColumnDto(
    int Ordinal,
    string SourceName,
    string PublicAlias,
    PublicationDataType DataType,
    bool IsNullable,
    bool IsReadable,
    bool IsFilterable,
    bool IsSortable,
    bool IsWritable,
    bool IsKey,
    int? KeyOrdinal,
    bool IsConcurrencyToken,
    bool IsGenerated,
    PublicationForeignKeyDto? ForeignKey);

public sealed record PublicationForeignKeyDto(
    string ConstraintName,
    int Ordinal,
    int ColumnCount,
    string ReferencedSchema,
    string ReferencedObject,
    string ReferencedColumn,
    string DisplayColumn,
    IReadOnlyList<string> SearchColumns,
    PublicationLookupMode LookupMode);

public enum PublicationCatalogWriteStatus
{
    Saved,
    DuplicateSlug,
    DuplicateVersion,
    Conflict,
}

public interface IPublicationCatalogStore
{
    Task<IReadOnlyList<Publication>> ListAsync(
        PublicationCatalogQuery query,
        CancellationToken cancellationToken);

    Task<Publication?> FindAsync(Guid publicationId, CancellationToken cancellationToken);

    Task<Publication?> FindBySlugAsync(string slug, CancellationToken cancellationToken);

    Task<IReadOnlyList<PublicationVersion>> ListVersionsAsync(
        Guid publicationId,
        CancellationToken cancellationToken);

    Task<PublicationVersion?> FindVersionAsync(
        Guid publicationId,
        Guid versionId,
        CancellationToken cancellationToken);

    Task<int> GetNextVersionNumberAsync(Guid publicationId, CancellationToken cancellationToken);

    Task<PublicationCatalogWriteStatus> AddPublicationAsync(
        Publication publication,
        CancellationToken cancellationToken);

    Task<PublicationCatalogWriteStatus> AddVersionAsync(
        PublicationVersion version,
        CancellationToken cancellationToken);

    Task<PublicationCatalogWriteStatus> UpdatePublicationAsync(
        Publication publication,
        DateTimeOffset expectedUpdatedAtUtc,
        CancellationToken cancellationToken);
}
