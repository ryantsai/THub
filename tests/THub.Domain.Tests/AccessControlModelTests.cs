using THub.Domain.Security;

namespace THub.Domain.Tests;

public sealed class AccessControlModelTests
{
    [Fact]
    public void Assignment_NormalizesWindowsPrincipalForUniqueness()
    {
        var assignment = new AccessRoleAssignment(
            Guid.NewGuid(),
            Guid.NewGuid(),
            AccessPrincipalKind.WindowsGroup,
            " Contoso\\THub Developers ");

        Assert.Equal("Contoso\\THub Developers", assignment.PrincipalName);
        Assert.Equal("CONTOSO\\THUB DEVELOPERS", assignment.NormalizedPrincipalName);
    }

    [Fact]
    public void SystemRole_CannotBeRenamed()
    {
        var role = new AccessRole(
            SystemRoleIds.SystemAdministrator,
            "System Administrator",
            "All access",
            SystemRoleKind.SystemAdministrator,
            DateTimeOffset.UtcNow,
            "test");

        Assert.Throws<InvalidOperationException>(() => role.Rename("Other", "Other"));
    }

    [Fact]
    public void ResourceGrant_NormalizesPermission()
    {
        var grant = new AccessResourceGrant(
            Guid.NewGuid(),
            Guid.NewGuid(),
            AccessResourceKind.Workflow,
            Guid.NewGuid(),
            "WORKFLOW.VIEW");

        Assert.Equal("workflow.view", grant.Permission);
    }
}
