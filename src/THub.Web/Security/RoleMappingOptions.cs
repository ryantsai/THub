namespace THub.Web.Security;

public sealed class RoleMappingOptions
{
    public const string SectionName = "Authorization:RoleMappings";

    public string DefaultAuthenticatedRole { get; init; } = nameof(AppRole.Viewer);
    public string[] Administrators { get; init; } = [];
    public string[] Designers { get; init; } = [];
    public string[] Operators { get; init; } = [];
    public string[] Viewers { get; init; } = [];
}

