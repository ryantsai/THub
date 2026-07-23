using THub.Domain.Publications;

namespace THub.Application.Publications;

public sealed class PublicationEditorService(
    IPublicationCatalogStore catalogStore,
    IPublicationChangeSetStore changeSetStore,
    IPublicationSourceDataReader sourceDataReader,
    PublicationAuthorizationService authorizationService,
    TimeProvider timeProvider)
{
    private readonly IPublicationCatalogStore _catalogStore =
        catalogStore ?? throw new ArgumentNullException(nameof(catalogStore));
    private readonly IPublicationChangeSetStore _changeSetStore =
        changeSetStore ?? throw new ArgumentNullException(nameof(changeSetStore));
    private readonly IPublicationSourceDataReader _sourceDataReader =
        sourceDataReader ?? throw new ArgumentNullException(nameof(sourceDataReader));
    private readonly PublicationAuthorizationService _authorizationService =
        authorizationService ?? throw new ArgumentNullException(nameof(authorizationService));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<PublicationResult<PublicationChangeSetDto>> StageAsync(
        StagePublicationChangeSetCommand command,
        CancellationToken cancellationToken)
    {
        if (command is null ||
            command.PublicationId == Guid.Empty ||
            command.Changes is null ||
            command.Changes.Count == 0)
        {
            return PublicationResultFactory.Validation<PublicationChangeSetDto>(
                "publication.change_command_invalid",
                "A publication, role set, and at least one staged change are required.");
        }

        var active = await FindActiveEditorVersionAsync(command.PublicationId, cancellationToken)
            .ConfigureAwait(false);
        if (!active.IsSuccess)
        {
            return CopyFailure<PublicationVersion, PublicationChangeSetDto>(active);
        }

        var requiredOperations = command.Changes
            .Where(change => change is not null && Enum.IsDefined(change.Operation))
            .Select(change => MapOperation(change.Operation))
            .Distinct()
            .ToArray();
        string? expectedGrantFingerprint = null;
        foreach (var operation in requiredOperations)
        {
            var authorization = await _authorizationService.AuthorizeAsync(
                    command.PublicationId,
                    command.Roles,
                    operation,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!authorization.IsSuccess)
            {
                return CopyFailure<PublicationAuthorizationDto, PublicationChangeSetDto>(authorization);
            }

            if (expectedGrantFingerprint is not null &&
                !string.Equals(
                    expectedGrantFingerprint,
                    authorization.Value!.GrantFingerprint,
                    StringComparison.Ordinal))
            {
                return PublicationResultFactory.Conflict<PublicationChangeSetDto>(
                    "publication.authorization_changed",
                    "Publication role grants changed while the change set was being validated. Retry the submission.");
            }

            expectedGrantFingerprint = authorization.Value!.GrantFingerprint;
        }

        var version = active.Value!;
        var validationProblem = PublicationChangeValidator.Validate(version, command.Changes);
        if (validationProblem is not null)
        {
            return PublicationResult<PublicationChangeSetDto>.Failure(
                validationProblem.Kind,
                validationProblem.Code,
                validationProblem.Message);
        }

        var foreignKeys = PublicationChangeValidator.ExtractForeignKeyTuples(version, command.Changes);
        if (foreignKeys.Problem is not null)
        {
            return PublicationResult<PublicationChangeSetDto>.Failure(
                foreignKeys.Problem.Kind,
                foreignKeys.Problem.Code,
                foreignKeys.Problem.Message);
        }

        if (foreignKeys.Tuples.Count > 0)
        {
            var resolution = await _sourceDataReader.ResolveForeignKeysAsync(
                    version,
                    foreignKeys.Tuples,
                    cancellationToken)
                .ConfigureAwait(false);
            if (resolution.Status == PublicationSourceReadStatus.SchemaChanged)
            {
                return PublicationResultFactory.Conflict<PublicationChangeSetDto>(
                    "publication.source_schema_changed",
                    "The source schema no longer matches the active publication version.");
            }

            if (resolution.Status != PublicationSourceReadStatus.Success || resolution.Value is null)
            {
                return PublicationResultFactory.Unavailable<PublicationChangeSetDto>(
                    "publication.foreign_key_validation_unavailable",
                    "Foreign-key values could not be validated against the approved source.");
            }

            var resolvedIds = resolution.Value.Labels
                .Select(label => label.RequestId)
                .ToHashSet();
            if (resolvedIds.Count != foreignKeys.Tuples.Count ||
                foreignKeys.Tuples.Any(tuple => !resolvedIds.Contains(tuple.RequestId)))
            {
                return PublicationResultFactory.Validation<PublicationChangeSetDto>(
                    "publication.foreign_key_not_found",
                    "One or more foreign-key tuples do not exist in the approved referenced table.");
            }

            var current = await FindActiveEditorVersionAsync(command.PublicationId, cancellationToken)
                .ConfigureAwait(false);
            if (!current.IsSuccess || current.Value!.Id != version.Id)
            {
                return PublicationResultFactory.Conflict<PublicationChangeSetDto>(
                    "publication.change_version_stale",
                    "The active publication version changed while foreign-key values were validated.");
            }

            foreach (var operation in requiredOperations)
            {
                var authorization = await _authorizationService.AuthorizeAsync(
                        command.PublicationId,
                        command.Roles,
                        operation,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!authorization.IsSuccess)
                {
                    return CopyFailure<PublicationAuthorizationDto, PublicationChangeSetDto>(authorization);
                }
            }
        }

        if (expectedGrantFingerprint is null)
        {
            return PublicationResultFactory.Conflict<PublicationChangeSetDto>(
                "publication.authorization_changed",
                "Publication role grants could not be fixed for this change set. Retry the submission.");
        }

        try
        {
            var changeSetId = Guid.NewGuid();
            var changes = command.Changes.Select(change => new PublicationChange(
                Guid.NewGuid(),
                changeSetId,
                change.Operation,
                change.KeyJson,
                change.BeforeJson,
                change.AfterJson));
            var changeSet = new PublicationChangeSet(
                changeSetId,
                command.PublicationId,
                version.Id,
                changes,
                command.Actor,
                _timeProvider.GetUtcNow());
            var status = await _changeSetStore.AddAsync(
                    changeSet,
                    expectedGrantFingerprint,
                    cancellationToken)
                .ConfigureAwait(false);
            return status == PublicationChangeSetWriteStatus.Saved
                ? PublicationResult<PublicationChangeSetDto>.Success(PublicationDtoMapper.ToDto(changeSet))
                : PublicationResultFactory.Conflict<PublicationChangeSetDto>(
                    "publication.change_stage_conflict",
                    "The change set could not be staged because publication state changed.");
        }
        catch (Exception exception) when (IsDomainException(exception))
        {
            return PublicationResultFactory.FromDomainException<PublicationChangeSetDto>(exception);
        }
    }

    public async Task<PublicationResult<PublicationChangeSetDto>> ReviewAsync(
        ReviewPublicationChangeSetCommand command,
        CancellationToken cancellationToken)
    {
        if (command is null || command.PublicationId == Guid.Empty || command.ChangeSetId == Guid.Empty)
        {
            return PublicationResultFactory.Validation<PublicationChangeSetDto>(
                "publication.review_command_invalid",
                "Publication and change-set identifiers are required.");
        }

        if (!Enum.IsDefined(command.Decision))
        {
            return PublicationResultFactory.Validation<PublicationChangeSetDto>(
                "publication.review_decision_invalid",
                "The review decision is invalid.");
        }

        var authorization = await _authorizationService.AuthorizeAsync(
                command.PublicationId,
                command.Roles,
                PublicationOperation.Approve,
                cancellationToken)
            .ConfigureAwait(false);
        if (!authorization.IsSuccess)
        {
            return CopyFailure<PublicationAuthorizationDto, PublicationChangeSetDto>(authorization);
        }

        var active = await FindActiveEditorVersionAsync(command.PublicationId, cancellationToken)
            .ConfigureAwait(false);
        if (!active.IsSuccess)
        {
            return CopyFailure<PublicationVersion, PublicationChangeSetDto>(active);
        }

        var changeSet = await _changeSetStore.FindAsync(
                command.PublicationId,
                command.ChangeSetId,
                cancellationToken)
            .ConfigureAwait(false);
        if (changeSet is null)
        {
            return PublicationResultFactory.NotFound<PublicationChangeSetDto>(
                "publication.change_set_not_found",
                "The publication change set was not found.");
        }

        if (changeSet.PublicationVersionId != active.Value!.Id)
        {
            return PublicationResultFactory.Conflict<PublicationChangeSetDto>(
                "publication.change_version_stale",
                "The change set targets a publication version that is no longer active.");
        }

        try
        {
            var expectedUpdatedAt = changeSet.UpdatedAtUtc;
            var now = _timeProvider.GetUtcNow();
            if (command.Decision == PublicationChangeReviewDecision.Approve)
            {
                changeSet.Approve(command.Actor, now, command.Comment);
            }
            else
            {
                changeSet.Reject(command.Actor, command.Comment ?? string.Empty, now);
            }

            var status = await _changeSetStore
                .UpdateAsync(
                    changeSet,
                    expectedUpdatedAt,
                    authorization.Value!.GrantFingerprint,
                    cancellationToken)
                .ConfigureAwait(false);
            return status switch
            {
                PublicationChangeSetWriteStatus.Saved =>
                    PublicationResult<PublicationChangeSetDto>.Success(PublicationDtoMapper.ToDto(changeSet)),
                PublicationChangeSetWriteStatus.NotFound =>
                    PublicationResultFactory.NotFound<PublicationChangeSetDto>(
                        "publication.change_set_not_found",
                        "The publication change set was not found."),
                _ => PublicationResultFactory.Conflict<PublicationChangeSetDto>(
                    "publication.change_review_conflict",
                    "The change set was reviewed concurrently."),
            };
        }
        catch (Exception exception) when (IsDomainException(exception))
        {
            return PublicationResultFactory.FromDomainException<PublicationChangeSetDto>(exception);
        }
    }

    private async Task<PublicationResult<PublicationVersion>> FindActiveEditorVersionAsync(
        Guid publicationId,
        CancellationToken cancellationToken)
    {
        var publication = await _catalogStore.FindAsync(publicationId, cancellationToken)
            .ConfigureAwait(false);
        if (publication is null)
        {
            return PublicationResultFactory.NotFound<PublicationVersion>(
                "publication.not_found",
                "The publication was not found.");
        }

        if (publication.Kind != PublicationKind.Editor ||
            publication.State != PublicationState.Active ||
            publication.ActiveVersionId is not Guid versionId)
        {
            return PublicationResultFactory.Conflict<PublicationVersion>(
                "publication.editor_not_active",
                "Staged editor operations require an active editor publication.");
        }

        var version = await _catalogStore.FindVersionAsync(publicationId, versionId, cancellationToken)
            .ConfigureAwait(false);
        return version is null
            ? PublicationResultFactory.Conflict<PublicationVersion>(
                "publication.active_version_missing",
                "The active publication version is unavailable.")
            : PublicationResult<PublicationVersion>.Success(version);
    }

    private static PublicationOperation MapOperation(PublicationChangeOperation operation) => operation switch
    {
        PublicationChangeOperation.Insert => PublicationOperation.Insert,
        PublicationChangeOperation.Update => PublicationOperation.Update,
        PublicationChangeOperation.Delete => PublicationOperation.Delete,
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static PublicationResult<TTarget> CopyFailure<TSource, TTarget>(
        PublicationResult<TSource> result)
    {
        var problem = result.Problem ?? throw new InvalidOperationException("A failed result requires a problem.");
        return PublicationResult<TTarget>.Failure(problem.Kind, problem.Code, problem.Message);
    }

    private static bool IsDomainException(Exception exception) =>
        exception is ArgumentException or InvalidOperationException or OverflowException;
}
