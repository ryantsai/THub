using THub.Domain.Publications;

namespace THub.Application.Publications;

public sealed class PublicationTokenService(
    IPublicationCatalogStore catalogStore,
    IPublicationTokenStore tokenStore,
    PublicationTokenGenerator tokenGenerator,
    TimeProvider timeProvider)
{
    private const int CurrentAlgorithmVersion = 1;
    private const int MaximumSelectorGenerationAttempts = 3;

    private readonly IPublicationCatalogStore _catalogStore =
        catalogStore ?? throw new ArgumentNullException(nameof(catalogStore));
    private readonly IPublicationTokenStore _tokenStore =
        tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
    private readonly PublicationTokenGenerator _tokenGenerator =
        tokenGenerator ?? throw new ArgumentNullException(nameof(tokenGenerator));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<PublicationResult<CreatedPublicationTokenDto>> CreateAsync(
        CreatePublicationTokenCommand command,
        CancellationToken cancellationToken)
    {
        if (command is null || command.PublicationId == Guid.Empty)
        {
            return PublicationResultFactory.Validation<CreatedPublicationTokenDto>(
                "publication.token_command_invalid",
                "A publication identifier and token command are required.");
        }

        var publication = await _catalogStore.FindAsync(command.PublicationId, cancellationToken)
            .ConfigureAwait(false);
        if (publication is null)
        {
            return PublicationResultFactory.NotFound<CreatedPublicationTokenDto>(
                "publication.not_found",
                "The publication was not found.");
        }

        if (publication.Kind != PublicationKind.RestApi ||
            publication.State != PublicationState.Active ||
            publication.ActiveVersionId is null)
        {
            return PublicationResultFactory.Conflict<CreatedPublicationTokenDto>(
                "publication.token_requires_active_rest",
                "Bearer tokens can be created only for an active REST publication.");
        }

        var now = _timeProvider.GetUtcNow();
        for (var attempt = 0; attempt < MaximumSelectorGenerationAttempts; attempt++)
        {
            var material = _tokenGenerator.Generate();
            try
            {
                var token = new PublicationAccessToken(
                    Guid.NewGuid(),
                    publication.Id,
                    command.Name,
                    material.Selector,
                    Convert.ToBase64String(material.Verifier),
                    CurrentAlgorithmVersion,
                    material.DisplayPrefix,
                    command.Actor,
                    now,
                    command.ExpiresAtUtc);
                var writeStatus = await _tokenStore.AddAsync(token, cancellationToken)
                    .ConfigureAwait(false);
                if (writeStatus == PublicationTokenWriteStatus.Saved)
                {
                    return PublicationResult<CreatedPublicationTokenDto>.Success(
                        new CreatedPublicationTokenDto(
                            token.Id,
                            token.PublicationId,
                            token.Name,
                            token.DisplayPrefix,
                            material.PlaintextToken,
                            token.CreatedAtUtc,
                            token.ExpiresAtUtc));
                }

                if (writeStatus != PublicationTokenWriteStatus.DuplicateSelector)
                {
                    return PublicationResultFactory.Conflict<CreatedPublicationTokenDto>(
                        "publication.token_create_conflict",
                        "The token could not be created because publication state changed.");
                }
            }
            catch (Exception exception) when (IsDomainException(exception))
            {
                return PublicationResultFactory.FromDomainException<CreatedPublicationTokenDto>(exception);
            }
        }

        return PublicationResultFactory.Unavailable<CreatedPublicationTokenDto>(
            "publication.token_generation_unavailable",
            "A unique token selector could not be generated. Try again.");
    }

    public async Task<PublicationResult<IReadOnlyList<PublicationTokenMetadataDto>>> ListAsync(
        Guid publicationId,
        CancellationToken cancellationToken)
    {
        if (publicationId == Guid.Empty)
        {
            return PublicationResultFactory.Validation<IReadOnlyList<PublicationTokenMetadataDto>>(
                "publication.id_required",
                "A publication identifier is required.");
        }

        var publication = await _catalogStore.FindAsync(publicationId, cancellationToken)
            .ConfigureAwait(false);
        if (publication is null)
        {
            return PublicationResultFactory.NotFound<IReadOnlyList<PublicationTokenMetadataDto>>(
                "publication.not_found",
                "The publication was not found.");
        }

        var now = _timeProvider.GetUtcNow();
        var tokens = await _tokenStore.ListAsync(publicationId, cancellationToken).ConfigureAwait(false);
        return PublicationResult<IReadOnlyList<PublicationTokenMetadataDto>>.Success(
            tokens
                .OrderByDescending(token => token.CreatedAtUtc)
                .Select(token => ToMetadata(token, now))
                .ToArray());
    }

    public async Task<PublicationResult<PublicationCompleted>> RevokeAsync(
        RevokePublicationTokenCommand command,
        CancellationToken cancellationToken)
    {
        if (command is null || command.PublicationId == Guid.Empty || command.TokenId == Guid.Empty)
        {
            return PublicationResultFactory.Validation<PublicationCompleted>(
                "publication.token_id_required",
                "Publication and token identifiers are required.");
        }

        if (string.IsNullOrWhiteSpace(command.Actor))
        {
            return PublicationResultFactory.Validation<PublicationCompleted>(
                "publication.actor_required",
                "An authenticated actor is required.");
        }

        var status = await _tokenStore.RevokeAsync(
                command.PublicationId,
                command.TokenId,
                command.Actor,
                _timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        return status switch
        {
            PublicationTokenRevocationStatus.Revoked =>
                PublicationResult<PublicationCompleted>.Success(PublicationCompleted.Value),
            PublicationTokenRevocationStatus.NotFound =>
                PublicationResultFactory.NotFound<PublicationCompleted>(
                    "publication.token_not_found",
                    "The publication token was not found."),
            PublicationTokenRevocationStatus.AlreadyRevoked =>
                PublicationResultFactory.Conflict<PublicationCompleted>(
                    "publication.token_already_revoked",
                    "The publication token has already been revoked."),
            _ => PublicationResultFactory.Conflict<PublicationCompleted>(
                "publication.token_revoke_conflict",
                "The token changed while it was being revoked."),
        };
    }

    /// <summary>
    /// Verifies an opaque credential and binds it to the active route version without incrementing
    /// usage. Publication hosts use this result to perform per-token admission before metering.
    /// </summary>
    public async Task<PublicationResult<ValidatedPublicationTokenDto>> ValidateForAdmissionAsync(
        string publicationSlug,
        string? opaqueToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(publicationSlug) ||
            !_tokenGenerator.TryReadSelector(opaqueToken, out var selector))
        {
            return InvalidValidatedCredential();
        }

        var token = await _tokenStore.FindBySelectorAsync(selector, cancellationToken)
            .ConfigureAwait(false);
        if (token is null || token.AlgorithmVersion != CurrentAlgorithmVersion)
        {
            return InvalidValidatedCredential();
        }

        byte[] expectedVerifier;
        try
        {
            expectedVerifier = Convert.FromBase64String(token.Verifier);
        }
        catch (FormatException)
        {
            return PublicationResultFactory.Unavailable<ValidatedPublicationTokenDto>(
                "publication.token_verifier_unavailable",
                "Token authentication is temporarily unavailable.");
        }

        if (!_tokenGenerator.Verify(opaqueToken, expectedVerifier))
        {
            return InvalidValidatedCredential();
        }

        var now = _timeProvider.GetUtcNow();
        if (!token.IsUsableAt(now))
        {
            return InvalidValidatedCredential();
        }

        string normalizedSlug;
        try
        {
            normalizedSlug = Publication.NormalizeSlug(publicationSlug);
        }
        catch (ArgumentException)
        {
            return InvalidValidatedCredential();
        }

        var publication = await _catalogStore.FindBySlugAsync(normalizedSlug, cancellationToken)
            .ConfigureAwait(false);
        if (publication is null ||
            publication.Id != token.PublicationId ||
            publication.Kind != PublicationKind.RestApi ||
            publication.State != PublicationState.Active ||
            publication.ActiveVersionId is not Guid versionId)
        {
            return InvalidValidatedCredential();
        }

        var version = await _catalogStore.FindVersionAsync(
                publication.Id,
                versionId,
                cancellationToken)
            .ConfigureAwait(false);
        if (version is null)
        {
            return PublicationResultFactory.Unavailable<ValidatedPublicationTokenDto>(
                "publication.active_version_unavailable",
                "Token authentication is temporarily unavailable.");
        }

        return PublicationResult<ValidatedPublicationTokenDto>.Success(new(
            token.Id,
            publication.Id,
            version.Id,
            version.Settings.RequestsPerWindow,
            version.Settings.RateLimitWindowSeconds,
            version.Settings.MaximumConcurrentRequests,
            version.Settings.RequestTimeoutSeconds,
            version.Settings.MaximumResponseBytes));
    }

    /// <summary>
    /// Atomically rechecks and meters a request after its rate and concurrency gates accepted it.
    /// Failure is closed: callers must not query or return publication data unless this succeeds.
    /// </summary>
    public async Task<PublicationResult<AuthenticatedPublicationTokenDto>> RecordAcceptedUseAsync(
        ValidatedPublicationTokenDto validated,
        CancellationToken cancellationToken)
    {
        if (validated is null
            || validated.TokenId == Guid.Empty
            || validated.PublicationId == Guid.Empty
            || validated.PublicationVersionId == Guid.Empty)
        {
            return InvalidCredential();
        }

        var now = _timeProvider.GetUtcNow();
        var meteringStatus = await _tokenStore.TryRecordAcceptedUseAsync(
                validated.TokenId,
                validated.PublicationId,
                validated.PublicationVersionId,
                now,
                cancellationToken)
            .ConfigureAwait(false);
        return meteringStatus switch
        {
            PublicationAcceptedUseStatus.Recorded =>
                PublicationResult<AuthenticatedPublicationTokenDto>.Success(
                    new AuthenticatedPublicationTokenDto(
                        validated.TokenId,
                        validated.PublicationId,
                        validated.PublicationVersionId,
                        now)),
            PublicationAcceptedUseStatus.MeteringUnavailable =>
                PublicationResultFactory.Unavailable<AuthenticatedPublicationTokenDto>(
                    "publication.token_metering_unavailable",
                    "The accepted request could not be metered, so access was denied."),
            _ => InvalidCredential(),
        };
    }

    /// <summary>
    /// Compatibility composition for non-host callers. The public REST host must use the split
    /// validate/admit/meter sequence so rejected requests are not counted.
    /// </summary>
    public async Task<PublicationResult<AuthenticatedPublicationTokenDto>> AuthenticateAndRecordAcceptedUseAsync(
        string publicationSlug,
        string? opaqueToken,
        CancellationToken cancellationToken)
    {
        var validated = await ValidateForAdmissionAsync(
                publicationSlug,
                opaqueToken,
                cancellationToken)
            .ConfigureAwait(false);
        if (!validated.IsSuccess)
        {
            var problem = validated.Problem!;
            return PublicationResult<AuthenticatedPublicationTokenDto>.Failure(
                problem.Kind,
                problem.Code,
                problem.Message);
        }

        return await RecordAcceptedUseAsync(validated.Value!, cancellationToken).ConfigureAwait(false);
    }

    private static PublicationTokenMetadataDto ToMetadata(
        PublicationAccessToken token,
        DateTimeOffset now) =>
        new(
            token.Id,
            token.PublicationId,
            token.Name,
            token.DisplayPrefix,
            token.GetStatus(now),
            token.CreatedBy,
            token.CreatedAtUtc,
            token.ExpiresAtUtc,
            token.RevokedBy,
            token.RevokedAtUtc,
            token.AcceptedRequestCount,
            token.LastUsedAtUtc);

    private static PublicationResult<AuthenticatedPublicationTokenDto> InvalidCredential() =>
        PublicationResultFactory.Unauthorized<AuthenticatedPublicationTokenDto>(
            "publication.token_invalid",
            "The bearer token is invalid or unavailable.");

    private static PublicationResult<ValidatedPublicationTokenDto> InvalidValidatedCredential() =>
        PublicationResultFactory.Unauthorized<ValidatedPublicationTokenDto>(
            "publication.token_invalid",
            "The bearer token is invalid or unavailable.");

    private static bool IsDomainException(Exception exception) =>
        exception is ArgumentException or InvalidOperationException or OverflowException;
}
