namespace THub.Web.Security;

public sealed class AuthorizationBootstrapOptions
{
    public const string SectionName = "Authorization:Bootstrap";

    public string[] SystemAdministratorUsers { get; init; } = [];
    public string[] SystemAdministratorGroups { get; init; } = [];
    public string[] DeveloperUsers { get; init; } = [];
    public string[] DeveloperGroups { get; init; } = [];
}
