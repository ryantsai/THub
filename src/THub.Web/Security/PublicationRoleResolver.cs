using System.Security.Claims;

namespace THub.Web.Security;

public sealed class PublicationRoleResolver(AccessControlEvaluator evaluator)
{
    public async Task<IReadOnlyList<Guid>> ResolveAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default) =>
        (await evaluator.ResolveRoleIdsAsync(user, cancellationToken).ConfigureAwait(false))
        .Order()
        .ToArray();
}
