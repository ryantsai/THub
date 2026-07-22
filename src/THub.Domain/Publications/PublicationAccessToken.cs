namespace THub.Domain.Publications;

public sealed class PublicationAccessToken
{
    public const int MaximumNameLength = 100;
    public const int MaximumSelectorLength = 128;
    public const int MaximumVerifierLength = 512;

    private PublicationAccessToken()
    {
    }

    public PublicationAccessToken(
        Guid id,
        Guid publicationId,
        string name,
        string selector,
        string verifier,
        int algorithmVersion,
        string displayPrefix,
        string createdBy,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        Id = PublicationGuard.RequireId(id, nameof(id));
        PublicationId = PublicationGuard.RequireId(publicationId, nameof(publicationId));
        Name = PublicationGuard.Require(name, nameof(name), MaximumNameLength);
        Selector = RequireTokenMetadata(selector, nameof(selector), 8, MaximumSelectorLength);
        Verifier = PublicationGuard.Require(verifier, nameof(verifier), MaximumVerifierLength);
        if (Verifier.Length < 32)
        {
            throw new ArgumentOutOfRangeException(
                nameof(verifier),
                "A token verifier must contain at least 32 characters of encoded metadata.");
        }

        if (algorithmVersion < 1 || algorithmVersion > 1_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(algorithmVersion),
                algorithmVersion,
                "Algorithm version must be between 1 and 1000.");
        }

        AlgorithmVersion = algorithmVersion;
        DisplayPrefix = RequireTokenMetadata(displayPrefix, nameof(displayPrefix), 4, 32);
        CreatedBy = PublicationGuard.Require(createdBy, nameof(createdBy), Publication.MaximumIdentityLength);
        CreatedAtUtc = PublicationGuard.AsUtc(createdAtUtc);
        ExpiresAtUtc = PublicationGuard.AsUtc(expiresAtUtc);
        if (ExpiresAtUtc <= CreatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc), "Token expiry must be after creation.");
        }
    }

    public Guid Id { get; private set; }

    public Guid PublicationId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string Selector { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the encoded one-way verifier. This is verifier metadata, never the bearer secret.
    /// </summary>
    public string Verifier { get; private set; } = string.Empty;

    public int AlgorithmVersion { get; private set; }

    public string DisplayPrefix { get; private set; } = string.Empty;

    public string CreatedBy { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public string? RevokedBy { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public long AcceptedRequestCount { get; private set; }

    public DateTimeOffset? LastUsedAtUtc { get; private set; }

    public PublicationAccessTokenStatus GetStatus(DateTimeOffset atUtc)
    {
        if (RevokedAtUtc is not null)
        {
            return PublicationAccessTokenStatus.Revoked;
        }

        return PublicationGuard.AsUtc(atUtc) >= ExpiresAtUtc
            ? PublicationAccessTokenStatus.Expired
            : PublicationAccessTokenStatus.Active;
    }

    public bool IsUsableAt(DateTimeOffset atUtc) => GetStatus(atUtc) == PublicationAccessTokenStatus.Active;

    public void Revoke(string revokedBy, DateTimeOffset revokedAtUtc)
    {
        if (RevokedAtUtc is not null)
        {
            throw new InvalidOperationException("Token has already been revoked.");
        }

        RevokedBy = PublicationGuard.Require(revokedBy, nameof(revokedBy), Publication.MaximumIdentityLength);
        RevokedAtUtc = PublicationGuard.NotBefore(
            revokedAtUtc,
            LastUsedAtUtc ?? CreatedAtUtc,
            nameof(revokedAtUtc));
    }

    public void RecordAcceptedRequest(DateTimeOffset acceptedAtUtc)
    {
        var acceptedAt = PublicationGuard.NotBefore(
            acceptedAtUtc,
            LastUsedAtUtc ?? CreatedAtUtc,
            nameof(acceptedAtUtc));
        if (!IsUsableAt(acceptedAt))
        {
            throw new InvalidOperationException("Only an active token can record an accepted request.");
        }

        AcceptedRequestCount = checked(AcceptedRequestCount + 1);
        LastUsedAtUtc = acceptedAt;
    }

    private static string RequireTokenMetadata(
        string value,
        string parameterName,
        int minimumLength,
        int maximumLength)
    {
        var normalized = PublicationGuard.Require(value, parameterName, maximumLength);
        if (normalized.Length < minimumLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Value must contain at least {minimumLength} characters.");
        }

        if (normalized.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new ArgumentException("Value must use base64url-safe characters.", parameterName);
        }

        return normalized;
    }
}
