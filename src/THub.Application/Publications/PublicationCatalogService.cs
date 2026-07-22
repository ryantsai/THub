using THub.Domain.Publications;

namespace THub.Application.Publications;

public sealed class PublicationCatalogService(
    IPublicationCatalogStore store,
    TimeProvider timeProvider)
{
    private readonly IPublicationCatalogStore _store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<PublicationResult<IReadOnlyList<PublicationSummaryDto>>> ListAsync(
        PublicationCatalogQuery query,
        CancellationToken cancellationToken)
    {
        if (query is null)
        {
            return PublicationResultFactory.Validation<IReadOnlyList<PublicationSummaryDto>>(
                "publication.query_required",
                "A publication query is required.");
        }

        if (query.Kind is not null && !Enum.IsDefined(query.Kind.Value))
        {
            return PublicationResultFactory.Validation<IReadOnlyList<PublicationSummaryDto>>(
                "publication.kind_invalid",
                "The publication kind is invalid.");
        }

        if (query.State is not null && !Enum.IsDefined(query.State.Value))
        {
            return PublicationResultFactory.Validation<IReadOnlyList<PublicationSummaryDto>>(
                "publication.state_invalid",
                "The publication state is invalid.");
        }

        var publications = await _store.ListAsync(query, cancellationToken).ConfigureAwait(false);
        return PublicationResult<IReadOnlyList<PublicationSummaryDto>>.Success(
            publications.Select(PublicationDtoMapper.ToSummary).ToArray());
    }

    public async Task<PublicationResult<PublicationDetailDto>> GetAsync(
        Guid publicationId,
        CancellationToken cancellationToken)
    {
        if (publicationId == Guid.Empty)
        {
            return PublicationResultFactory.Validation<PublicationDetailDto>(
                "publication.id_required",
                "A publication identifier is required.");
        }

        var publication = await _store.FindAsync(publicationId, cancellationToken).ConfigureAwait(false);
        if (publication is null)
        {
            return PublicationResultFactory.NotFound<PublicationDetailDto>(
                "publication.not_found",
                "The publication was not found.");
        }

        var versions = await _store.ListVersionsAsync(publicationId, cancellationToken).ConfigureAwait(false);
        return PublicationResult<PublicationDetailDto>.Success(
            PublicationDtoMapper.ToDetail(publication, versions));
    }

    public async Task<PublicationResult<PublicationVersionDto>> GetVersionAsync(
        Guid publicationId,
        Guid versionId,
        CancellationToken cancellationToken)
    {
        if (publicationId == Guid.Empty || versionId == Guid.Empty)
        {
            return PublicationResultFactory.Validation<PublicationVersionDto>(
                "publication.version_id_required",
                "Publication and version identifiers are required.");
        }

        var version = await _store.FindVersionAsync(publicationId, versionId, cancellationToken)
            .ConfigureAwait(false);
        return version is null
            ? PublicationResultFactory.NotFound<PublicationVersionDto>(
                "publication.version_not_found",
                "The publication version was not found.")
            : PublicationResult<PublicationVersionDto>.Success(PublicationDtoMapper.ToDto(version));
    }

    public async Task<PublicationResult<IReadOnlyList<PublicationVersionSummaryDto>>> ListVersionsAsync(
        Guid publicationId,
        CancellationToken cancellationToken)
    {
        if (publicationId == Guid.Empty)
        {
            return PublicationResultFactory.Validation<IReadOnlyList<PublicationVersionSummaryDto>>(
                "publication.id_required",
                "A publication identifier is required.");
        }

        var publication = await _store.FindAsync(publicationId, cancellationToken).ConfigureAwait(false);
        if (publication is null)
        {
            return PublicationResultFactory.NotFound<IReadOnlyList<PublicationVersionSummaryDto>>(
                "publication.not_found",
                "The publication was not found.");
        }

        var versions = await _store.ListVersionsAsync(publicationId, cancellationToken).ConfigureAwait(false);
        return PublicationResult<IReadOnlyList<PublicationVersionSummaryDto>>.Success(
            versions
                .OrderByDescending(version => version.VersionNumber)
                .Select(PublicationDtoMapper.ToSummary)
                .ToArray());
    }

    public async Task<PublicationResult<PublicationDetailDto>> CreateAsync(
        CreatePublicationCommand command,
        CancellationToken cancellationToken)
    {
        if (command is null)
        {
            return PublicationResultFactory.Validation<PublicationDetailDto>(
                "publication.command_required",
                "A create-publication command is required.");
        }

        try
        {
            var now = _timeProvider.GetUtcNow();
            var publication = new Publication(
                Guid.NewGuid(),
                command.Slug,
                command.Name,
                command.Kind,
                command.Actor,
                now);
            var writeStatus = await _store.AddPublicationAsync(publication, cancellationToken)
                .ConfigureAwait(false);
            return writeStatus switch
            {
                PublicationCatalogWriteStatus.Saved => PublicationResult<PublicationDetailDto>.Success(
                    PublicationDtoMapper.ToDetail(publication, [])),
                PublicationCatalogWriteStatus.DuplicateSlug => PublicationResultFactory.Conflict<PublicationDetailDto>(
                    "publication.slug_exists",
                    "A publication with this route slug already exists."),
                _ => PublicationResultFactory.Conflict<PublicationDetailDto>(
                    "publication.create_conflict",
                    "The publication could not be created because its state changed."),
            };
        }
        catch (Exception exception) when (IsDomainException(exception))
        {
            return PublicationResultFactory.FromDomainException<PublicationDetailDto>(exception);
        }
    }

    public async Task<PublicationResult<PublicationVersionDto>> CreateVersionAsync(
        CreatePublicationVersionCommand command,
        CancellationToken cancellationToken)
    {
        if (command is null)
        {
            return PublicationResultFactory.Validation<PublicationVersionDto>(
                "publication.command_required",
                "A create-version command is required.");
        }

        if (command.PublicationId == Guid.Empty)
        {
            return PublicationResultFactory.Validation<PublicationVersionDto>(
                "publication.id_required",
                "A publication identifier is required.");
        }

        var publication = await _store.FindAsync(command.PublicationId, cancellationToken)
            .ConfigureAwait(false);
        if (publication is null)
        {
            return PublicationResultFactory.NotFound<PublicationVersionDto>(
                "publication.not_found",
                "The publication was not found.");
        }

        if (publication.State == PublicationState.Archived)
        {
            return PublicationResultFactory.Conflict<PublicationVersionDto>(
                "publication.archived",
                "Archived publications cannot receive new versions.");
        }

        if (command.Columns is null || command.Settings is null)
        {
            return PublicationResultFactory.Validation<PublicationVersionDto>(
                "publication.version_definition_required",
                "Settings and column definitions are required.");
        }

        if (publication.Kind == PublicationKind.RestApi &&
            (command.ConcurrencyMode != PublicationConcurrencyMode.ReadOnly ||
             command.Columns.Any(column => column.IsWritable)))
        {
            return PublicationResultFactory.Validation<PublicationVersionDto>(
                "publication.rest_read_only",
                "REST publications are read-only and cannot expose writable columns.");
        }

        try
        {
            var versionId = Guid.NewGuid();
            var columns = command.Columns.Select(column => CreateColumn(versionId, column)).ToArray();
            var versionNumber = await _store
                .GetNextVersionNumberAsync(command.PublicationId, cancellationToken)
                .ConfigureAwait(false);
            var version = new PublicationVersion(
                versionId,
                command.PublicationId,
                versionNumber,
                command.ConnectionId,
                command.SourceSchema,
                command.SourceObject,
                command.SourceObjectKind,
                command.SchemaFingerprint,
                command.ConcurrencyMode,
                command.Settings,
                columns,
                command.Actor,
                _timeProvider.GetUtcNow());
            var writeStatus = await _store.AddVersionAsync(version, cancellationToken)
                .ConfigureAwait(false);
            return writeStatus switch
            {
                PublicationCatalogWriteStatus.Saved =>
                    PublicationResult<PublicationVersionDto>.Success(PublicationDtoMapper.ToDto(version)),
                PublicationCatalogWriteStatus.DuplicateVersion =>
                    PublicationResultFactory.Conflict<PublicationVersionDto>(
                        "publication.version_conflict",
                        "Another version was created concurrently. Retry with the latest publication state."),
                _ => PublicationResultFactory.Conflict<PublicationVersionDto>(
                    "publication.version_create_conflict",
                    "The version could not be created because publication state changed."),
            };
        }
        catch (Exception exception) when (IsDomainException(exception))
        {
            return PublicationResultFactory.FromDomainException<PublicationVersionDto>(exception);
        }
    }

    public async Task<PublicationResult<PublicationDetailDto>> ActivateAsync(
        Guid publicationId,
        Guid versionId,
        string actor,
        CancellationToken cancellationToken)
    {
        if (publicationId == Guid.Empty || versionId == Guid.Empty)
        {
            return PublicationResultFactory.Validation<PublicationDetailDto>(
                "publication.version_id_required",
                "Publication and version identifiers are required.");
        }

        var publication = await _store.FindAsync(publicationId, cancellationToken).ConfigureAwait(false);
        if (publication is null)
        {
            return PublicationResultFactory.NotFound<PublicationDetailDto>(
                "publication.not_found",
                "The publication was not found.");
        }

        var version = await _store.FindVersionAsync(publicationId, versionId, cancellationToken)
            .ConfigureAwait(false);
        if (version is null)
        {
            return PublicationResultFactory.NotFound<PublicationDetailDto>(
                "publication.version_not_found",
                "The publication version was not found.");
        }

        if (publication.Kind == PublicationKind.RestApi &&
            (version.ConcurrencyMode != PublicationConcurrencyMode.ReadOnly ||
             version.Columns.Any(column => column.IsWritable)))
        {
            return PublicationResultFactory.Validation<PublicationDetailDto>(
                "publication.rest_read_only",
                "REST publications can activate only read-only versions.");
        }

        try
        {
            var expectedUpdatedAt = publication.UpdatedAtUtc;
            publication.ActivateVersion(version, actor, _timeProvider.GetUtcNow());
            var writeStatus = await _store
                .UpdatePublicationAsync(publication, expectedUpdatedAt, cancellationToken)
                .ConfigureAwait(false);
            if (writeStatus != PublicationCatalogWriteStatus.Saved)
            {
                return PublicationResultFactory.Conflict<PublicationDetailDto>(
                    "publication.activation_conflict",
                    "The publication changed while the version was being activated.");
            }

            var versions = await _store.ListVersionsAsync(publicationId, cancellationToken)
                .ConfigureAwait(false);
            return PublicationResult<PublicationDetailDto>.Success(
                PublicationDtoMapper.ToDetail(publication, versions));
        }
        catch (Exception exception) when (IsDomainException(exception))
        {
            return PublicationResultFactory.FromDomainException<PublicationDetailDto>(exception);
        }
    }

    public async Task<PublicationResult<PublicationDetailDto>> DisableAsync(
        Guid publicationId,
        string actor,
        CancellationToken cancellationToken)
    {
        if (publicationId == Guid.Empty)
        {
            return PublicationResultFactory.Validation<PublicationDetailDto>(
                "publication.id_required",
                "A publication identifier is required.");
        }

        var publication = await _store.FindAsync(publicationId, cancellationToken).ConfigureAwait(false);
        if (publication is null)
        {
            return PublicationResultFactory.NotFound<PublicationDetailDto>(
                "publication.not_found",
                "The publication was not found.");
        }

        try
        {
            var expectedUpdatedAt = publication.UpdatedAtUtc;
            publication.Disable(actor, _timeProvider.GetUtcNow());
            var writeStatus = await _store
                .UpdatePublicationAsync(publication, expectedUpdatedAt, cancellationToken)
                .ConfigureAwait(false);
            if (writeStatus != PublicationCatalogWriteStatus.Saved)
            {
                return PublicationResultFactory.Conflict<PublicationDetailDto>(
                    "publication.disable_conflict",
                    "The publication changed while it was being disabled.");
            }

            var versions = await _store.ListVersionsAsync(publicationId, cancellationToken)
                .ConfigureAwait(false);
            return PublicationResult<PublicationDetailDto>.Success(
                PublicationDtoMapper.ToDetail(publication, versions));
        }
        catch (Exception exception) when (IsDomainException(exception))
        {
            return PublicationResultFactory.FromDomainException<PublicationDetailDto>(exception);
        }
    }

    private static PublicationColumn CreateColumn(
        Guid versionId,
        CreatePublicationColumnCommand command)
    {
        var foreignKey = command.ForeignKey is null
            ? null
            : new PublicationForeignKey(
                command.ForeignKey.ConstraintName,
                command.ForeignKey.Ordinal,
                command.ForeignKey.ColumnCount,
                command.ForeignKey.ReferencedSchema,
                command.ForeignKey.ReferencedObject,
                command.ForeignKey.ReferencedColumn,
                command.ForeignKey.DisplayColumn,
                command.ForeignKey.SearchColumns,
                command.ForeignKey.LookupMode);
        return new PublicationColumn(
            Guid.NewGuid(),
            versionId,
            command.Ordinal,
            command.SourceName,
            command.PublicAlias,
            command.DataType,
            command.SourceTypeName,
            command.IsNullable,
            command.IsReadable,
            command.IsFilterable,
            command.IsSortable,
            command.IsWritable,
            command.IsKey,
            command.KeyOrdinal,
            command.IsConcurrencyToken,
            command.IsGenerated,
            command.MaximumLength,
            command.NumericPrecision,
            command.NumericScale,
            foreignKey);
    }

    private static bool IsDomainException(Exception exception) =>
        exception is ArgumentException or InvalidOperationException or OverflowException;
}
