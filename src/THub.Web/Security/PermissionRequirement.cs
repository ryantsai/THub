using Microsoft.AspNetCore.Authorization;

namespace THub.Web.Security;

public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

