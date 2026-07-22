using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace THub.Web.Security;

public sealed class PermissionAuthorizationHandler(IOptions<RoleMappingOptions> options)
    : AuthorizationHandler<PermissionRequirement>
{
    private static readonly IReadOnlyDictionary<AppRole, HashSet<string>> RolePermissions =
        new Dictionary<AppRole, HashSet<string>>
        {
            [AppRole.Viewer] = [Permissions.WorkflowView],
            [AppRole.Operator] =
            [
                Permissions.WorkflowView,
                Permissions.WorkflowExecute,
                Permissions.ScheduleManage
            ],
            [AppRole.Designer] =
            [
                Permissions.WorkflowView,
                Permissions.WorkflowEdit,
                Permissions.ConnectionManage,
                Permissions.PublicationManage
            ],
            [AppRole.Administrator] = [.. Permissions.All]
        };

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return Task.CompletedTask;
        }

        foreach (var role in ResolveRoles(context.User))
        {
            if (RolePermissions[role].Contains(requirement.Permission))
            {
                context.Succeed(requirement);
                break;
            }
        }

        return Task.CompletedTask;
    }

    private IEnumerable<AppRole> ResolveRoles(System.Security.Claims.ClaimsPrincipal user)
    {
        var mappings = options.Value;
        foreach (var role in Enum.GetValues<AppRole>())
        {
            var groups = role switch
            {
                AppRole.Administrator => mappings.Administrators,
                AppRole.Designer => mappings.Designers,
                AppRole.Operator => mappings.Operators,
                _ => mappings.Viewers
            };

            if (groups.Any(user.IsInRole))
            {
                yield return role;
            }
        }

        if (Enum.TryParse<AppRole>(mappings.DefaultAuthenticatedRole, true, out var defaultRole))
        {
            yield return defaultRole;
        }
    }
}

