using THub.Domain.Publications;

namespace THub.Application.Publications;

public sealed record CreatePublicationTokenCommand(
    Guid PublicationId,
    string Name,
    DateTimeOffset ExpiresAtUtc,
    string Actor);

public sealed record RevokePublicationTokenCommand(
    Guid PublicationId,
    Guid TokenId,
    string Actor);

public sealed record CreatedPublicationTokenDto(
    Guid Id,
    Guid PublicationId,
    string Name,
    string DisplayPrefix,
    string PlaintextToken,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record PublicationTokenMetadataDto(
    Guid Id,
    Guid PublicationId,
    string Name,
    string DisplayPrefix,
    PublicationAccessTokenStatus Status,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string? RevokedBy,
    DateTimeOffset? RevokedAtUtc,
    long AcceptedRequestCount,
    DateTimeOffset? LastUsedAtUtc);

public sealed record AuthenticatedPublicationTokenDto(
    Guid TokenId,
    Guid PublicationId,
    Guid PublicationVersionId,
    DateTimeOffset AcceptedAtUtc);

/// <summary>
/// A verified route/token binding that is safe to use as the key for process-local rate and
/// concurrency admission. It is deliberately not yet metered; callers must admit the request and
/// then call the atomic metering operation before reading any source data.
/// </summary>
public sealed record ValidatedPublicationTokenDto(
    Guid TokenId,
    Guid PublicationId,
    Guid PublicationVersionId,
    int RequestsPerWindow,
    int RateLimitWindowSeconds,
    int MaximumConcurrentRequests,
    int RequestTimeoutSeconds,
    int MaximumResponseBytes);

public enum PublicationTokenWriteStatus
{
    Saved,
    DuplicateSelector,
    Conflict,
}

public enum PublicationTokenRevocationStatus
{
    Revoked,
    NotFound,
    AlreadyRevoked,
    Conflict,
}

public enum PublicationAcceptedUseStatus
{
    Recorded,
    TokenUnavailable,
    PublicationUnavailable,
    MeteringUnavailable,
}

public interface IPublicationTokenStore
{
    Task<PublicationAccessToken?> FindBySelectorAsync(
        string selector,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PublicationAccessToken>> ListAsync(
        Guid publicationId,
        CancellationToken cancellationToken);

    Task<PublicationTokenWriteStatus> AddAsync(
        PublicationAccessToken accessToken,
        CancellationToken cancellationToken);

    Task<PublicationTokenRevocationStatus> RevokeAsync(
        Guid publicationId,
        Guid tokenId,
        string revokedBy,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically verifies that the token remains active, verifies that its REST publication and
    /// active version remain available, and increments AcceptedRequestCount with LastUsedAtUtc.
    /// Implementations must not report Recorded unless the counter update commits. Failure to
    /// guarantee metering must return MeteringUnavailable and fail the request closed.
    /// </summary>
    Task<PublicationAcceptedUseStatus> TryRecordAcceptedUseAsync(
        Guid tokenId,
        Guid publicationId,
        Guid publicationVersionId,
        DateTimeOffset acceptedAtUtc,
        CancellationToken cancellationToken);
}
