using Microsoft.AspNetCore.Components.Authorization;
using THub.Application.Auditing;

namespace THub.Web.Security;

public sealed class AuditViewerAuthorization(
    AuthenticationStateProvider authenticationStateProvider,
    AccessControlEvaluator accessControl) : IAuditViewerAuthorization
{
    public async Task<bool> CanViewAsync(CancellationToken cancellationToken)
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        return await accessControl.HasPermissionAsync(
            state.User,
            Permissions.AuditView,
            cancellationToken: cancellationToken);
    }
}
