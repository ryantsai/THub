using THub.Domain.Publications;

namespace THub.Application.Publications;

internal static class PublicationDtoMapper
{
    public static PublicationSummaryDto ToSummary(Publication publication) =>
        new(
            publication.Id,
            publication.Slug,
            publication.Name,
            publication.Kind,
            publication.State,
            publication.ActiveVersionId,
            publication.UpdatedAtUtc);

    public static PublicationDetailDto ToDetail(
        Publication publication,
        IReadOnlyList<PublicationVersion> versions) =>
        new(
            publication.Id,
            publication.Slug,
            publication.Name,
            publication.Kind,
            publication.State,
            publication.ActiveVersionId,
            publication.CreatedBy,
            publication.CreatedAtUtc,
            publication.UpdatedBy,
            publication.UpdatedAtUtc,
            versions
                .OrderByDescending(version => version.VersionNumber)
                .Select(ToSummary)
                .ToArray());

    public static PublicationVersionSummaryDto ToSummary(PublicationVersion version) =>
        new(
            version.Id,
            version.VersionNumber,
            version.ConnectionId,
            version.ApplyConnectionId,
            version.SourceSchema,
            version.SourceObject,
            version.ConcurrencyMode,
            version.CreatedAtUtc);

    public static PublicationVersionDto ToDto(PublicationVersion version) =>
        new(
            version.Id,
            version.PublicationId,
            version.VersionNumber,
            version.ConnectionId,
            version.ApplyConnectionId,
            version.SourceSchema,
            version.SourceObject,
            version.SourceObjectKind,
            version.SchemaFingerprint,
            version.ConcurrencyMode,
            version.Settings,
            version.Columns.OrderBy(column => column.Ordinal).Select(ToDto).ToArray(),
            version.CreatedBy,
            version.CreatedAtUtc);

    public static PublicationChangeSetDto ToDto(PublicationChangeSet changeSet) =>
        new(
            changeSet.Id,
            changeSet.PublicationId,
            changeSet.PublicationVersionId,
            changeSet.Status,
            changeSet.Changes.Count,
            changeSet.SubmittedBy,
            changeSet.SubmittedAtUtc,
            changeSet.ReviewedBy,
            changeSet.ReviewedAtUtc,
            changeSet.ReviewComment,
            changeSet.OutcomeDetail);

    private static PublicationColumnDto ToDto(PublicationColumn column) =>
        new(
            column.Ordinal,
            column.SourceName,
            column.PublicAlias,
            column.DataType,
            column.IsNullable,
            column.IsReadable,
            column.IsFilterable,
            column.IsSortable,
            column.IsWritable,
            column.IsKey,
            column.KeyOrdinal,
            column.IsConcurrencyToken,
            column.IsGenerated,
            column.ForeignKey is null ? null : ToDto(column.ForeignKey));

    private static PublicationForeignKeyDto ToDto(PublicationForeignKey foreignKey) =>
        new(
            foreignKey.ConstraintName,
            foreignKey.Ordinal,
            foreignKey.ColumnCount,
            foreignKey.ReferencedSchema,
            foreignKey.ReferencedObject,
            foreignKey.ReferencedColumn,
            foreignKey.DisplayColumn,
            foreignKey.SearchColumns,
            foreignKey.LookupMode);
}
