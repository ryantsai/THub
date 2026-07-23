namespace THub.Application.Tests;

internal static class PublicationRole
{
    public static readonly Guid Viewer = new("20000000-0000-0000-0000-000000000001");
    public static readonly Guid Operator = new("20000000-0000-0000-0000-000000000002");
    public static readonly Guid Designer = new("20000000-0000-0000-0000-000000000003");
    public static readonly Guid Administrator = new("20000000-0000-0000-0000-000000000004");
}
