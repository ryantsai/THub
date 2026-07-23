using THub.Domain.Publications;
using THub.Domain.Security;

namespace THub.Application.Publications;

public sealed class PublicationAuthorizationService(
    IPublicationCatalogStore catalogStore,
    IPublicationGrantStore grantStore)
{
    private readonly IPublicationCatalogStore _catalogStore =
        catalogStore ?? throw new ArgumentNullException(nameof(catalogStore));
    private readonly IPublicationGrantStore _grantStore =
        grantStore ?? throw new ArgumentNullException(nameof(grantStore));

    public async Task<PublicationResult<PublicationAuthorizationDto>> AuthorizeAsync(
        Guid publicationId,
        IReadOnlyCollection<Guid> roleIds,
        PublicationOperation operation,
        CancellationToken cancellationToken)
    {
        if (publicationId == Guid.Empty)
        {
            return PublicationResultFactory.Validation<PublicationAuthorizationDto>(
                "publication.id_required",
                "A publication identifier is required.");
        }

        if (!Enum.IsDefined(operation) ||
            roleIds is null ||
            roleIds.Any(roleId => roleId == Guid.Empty))
        {
            return PublicationResultFactory.Validation<PublicationAuthorizationDto>(
                "publication.authorization_invalid",
                "Publication roles and operation must be valid.");
        }

        var publication = await _catalogStore.FindAsync(publicationId, cancellationToken)
            .ConfigureAwait(false);
        if (publication is null)
        {
            return PublicationResultFactory.NotFound<PublicationAuthorizationDto>(
                "publication.not_found",
                "The publication was not found.");
        }

        var requestedRoleIds = roleIds.Distinct().ToHashSet();
        var grants = await _grantStore.ListAsync(publicationId, cancellationToken).ConfigureAwait(false);
        var grantFingerprint = PublicationGrantFingerprint.Compute(grants);
        if (requestedRoleIds.Contains(SystemRoleIds.SystemAdministrator))
        {
            return PublicationResult<PublicationAuthorizationDto>.Success(
                new PublicationAuthorizationDto(
                    publicationId,
                    operation,
                    [SystemRoleIds.SystemAdministrator],
                    grantFingerprint));
        }

        var effectiveRoles = grants
            .Where(grant => requestedRoleIds.Contains(grant.RoleId) && grant.Allows(operation))
            .Select(grant => grant.RoleId)
            .Distinct()
            .Order()
            .ToArray();
        return effectiveRoles.Length == 0
            ? PublicationResultFactory.Forbidden<PublicationAuthorizationDto>(
                "publication.operation_forbidden",
                "None of the caller's publication roles grants this operation.")
            : PublicationResult<PublicationAuthorizationDto>.Success(
                new PublicationAuthorizationDto(
                    publicationId,
                    operation,
                    effectiveRoles,
                    grantFingerprint));
    }
}
