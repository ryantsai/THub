using System.Security.Claims;
using Microsoft.Extensions.Options;
using THub.Domain.Publications;

namespace THub.Web.Security;

/// <summary>
/// Resolves the same configured Windows-group roles used by permission policies into the stable
/// role names stored by editor publication grants. Resource authorization remains in Application.
/// </summary>
public sealed class PublicationRoleResolver(IOptions<RoleMappingOptions> options)
{
    public IReadOnlyList<PublicationRole> Resolve(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Identity?.IsAuthenticated != true)
        {
            return [];
        }

        var mappings = options.Value;
        var roles = new HashSet<PublicationRole>();
        AddIfMatched(user, mappings.Viewers, PublicationRole.Viewer, roles);
        AddIfMatched(user, mappings.Operators, PublicationRole.Operator, roles);
        AddIfMatched(user, mappings.Designers, PublicationRole.Designer, roles);
        AddIfMatched(user, mappings.Administrators, PublicationRole.Administrator, roles);
        if (Enum.TryParse<PublicationRole>(
                mappings.DefaultAuthenticatedRole,
                ignoreCase: true,
                out var defaultRole))
        {
            roles.Add(defaultRole);
        }

        return roles.Order().ToArray();
    }

    private static void AddIfMatched(
        ClaimsPrincipal user,
        IEnumerable<string> groups,
        PublicationRole role,
        ISet<PublicationRole> roles)
    {
        if (groups.Any(group => !string.IsNullOrWhiteSpace(group) && user.IsInRole(group)))
        {
            roles.Add(role);
        }
    }
}
