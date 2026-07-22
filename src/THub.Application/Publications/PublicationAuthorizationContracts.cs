using THub.Domain.Publications;

namespace THub.Application.Publications;

public sealed record PublicationAuthorizationDto(
    Guid PublicationId,
    PublicationOperation Operation,
    IReadOnlyList<PublicationRole> EffectiveRoles,
    string GrantFingerprint);

public interface IPublicationGrantStore
{
    Task<IReadOnlyList<PublicationGrant>> ListAsync(
        Guid publicationId,
        CancellationToken cancellationToken);
}
