using Microsoft.AspNetCore.Authorization;

namespace THub.Web.Security;

public static class AuthorizationExtensions
{
    public static IServiceCollection AddTHubAuthorization(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AuthorizationBootstrapOptions>()
            .Bind(configuration.GetSection(AuthorizationBootstrapOptions.SectionName));
        services.AddScoped<AccessControlEvaluator>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddScoped<PublicationRoleResolver>();
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();

            foreach (var permission in Permissions.All)
            {
                options.AddPolicy(permission, policy =>
                    policy.Requirements.Add(new PermissionRequirement(permission)));
            }
        });

        return services;
    }
}
