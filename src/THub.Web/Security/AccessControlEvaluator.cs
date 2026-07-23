using System.Security.Claims;
using Microsoft.Extensions.Options;
using THub.Application.Security;
using THub.Domain.Security;

namespace THub.Web.Security;

public sealed class AccessControlEvaluator(
    IAccessControlStore store,
    IOptions<AuthorizationBootstrapOptions> bootstrapOptions)
{
    private readonly IAccessControlStore _store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly AuthorizationBootstrapOptions _bootstrap =
        bootstrapOptions?.Value ?? throw new ArgumentNullException(nameof(bootstrapOptions));

    public async Task<bool> HasPermissionAsync(
        ClaimsPrincipal user,
        string permission,
        AccessResourceKind? resourceKind = null,
        Guid? resourceId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Identity?.IsAuthenticated != true ||
            !SecurityPermissions.All.Contains(permission) ||
            (resourceKind is null) != (resourceId is null) ||
            resourceId == Guid.Empty)
        {
            return false;
        }

        var bootstrapRoles = ResolveBootstrapRoles(user);
        if (bootstrapRoles.Contains(SystemRoleIds.SystemAdministrator))
        {
            return true;
        }

        var snapshot = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var roleIds = ResolveAssignedRoleIds(user, snapshot);
        roleIds.UnionWith(bootstrapRoles);
        var roles = snapshot.Roles.Where(role => roleIds.Contains(role.Id)).ToArray();
        if (roles.Any(role => role.SystemRole == SystemRoleKind.SystemAdministrator))
        {
            return true;
        }

        if (roles.Any(role => role.Permissions.Contains(permission, StringComparer.Ordinal)))
        {
            return true;
        }

        return resourceKind is null
            ? roles.Any(role => role.ResourceGrants.Any(grant =>
                string.Equals(grant.Permission, permission, StringComparison.Ordinal)))
            : roles.Any(role => role.ResourceGrants.Any(grant =>
                grant.ResourceKind == resourceKind &&
                grant.ResourceId == resourceId &&
                string.Equals(grant.Permission, permission, StringComparison.Ordinal)));
    }

    public async Task<IReadOnlySet<Guid>> ResolveRoleIdsAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Identity?.IsAuthenticated != true)
        {
            return new HashSet<Guid>();
        }

        var bootstrapRoles = ResolveBootstrapRoles(user);
        if (bootstrapRoles.Contains(SystemRoleIds.SystemAdministrator))
        {
            return bootstrapRoles;
        }

        var snapshot = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var roleIds = ResolveAssignedRoleIds(user, snapshot);
        roleIds.UnionWith(bootstrapRoles);
        return roleIds;
    }

    private HashSet<Guid> ResolveBootstrapRoles(ClaimsPrincipal user)
    {
        var roles = new HashSet<Guid>();
        if (Matches(user, _bootstrap.SystemAdministratorUsers, _bootstrap.SystemAdministratorGroups))
        {
            roles.Add(SystemRoleIds.SystemAdministrator);
        }

        if (Matches(user, _bootstrap.DeveloperUsers, _bootstrap.DeveloperGroups))
        {
            roles.Add(SystemRoleIds.Developer);
        }

        return roles;
    }

    private static HashSet<Guid> ResolveAssignedRoleIds(
        ClaimsPrincipal user,
        AccessControlSnapshot snapshot)
    {
        var userName = user.Identity?.Name?.Trim();
        return snapshot.Roles
            .Where(role => role.Assignments.Any(assignment =>
                assignment.PrincipalKind == AccessPrincipalKind.User
                    ? string.Equals(
                        assignment.PrincipalName,
                        userName,
                        StringComparison.OrdinalIgnoreCase)
                    : user.IsInRole(assignment.PrincipalName)))
            .Select(role => role.Id)
            .ToHashSet();
    }

    private static bool Matches(
        ClaimsPrincipal user,
        IEnumerable<string> users,
        IEnumerable<string> groups)
    {
        var userName = user.Identity?.Name;
        return users.Any(candidate =>
                   !string.IsNullOrWhiteSpace(candidate) &&
                   string.Equals(candidate.Trim(), userName, StringComparison.OrdinalIgnoreCase)) ||
               groups.Any(group =>
                   !string.IsNullOrWhiteSpace(group) &&
                   user.IsInRole(group.Trim()));
    }
}
