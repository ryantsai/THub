using Microsoft.AspNetCore.Authorization;
namespace THub.Web.Security;

public sealed class PermissionAuthorizationHandler(AccessControlEvaluator evaluator)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        if (await evaluator.HasPermissionAsync(context.User, requirement.Permission)
            .ConfigureAwait(false))
        {
            context.Succeed(requirement);
        }
    }
}
