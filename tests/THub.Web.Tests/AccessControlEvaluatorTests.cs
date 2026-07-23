using System.Security.Claims;
using Microsoft.Extensions.Options;
using THub.Application.Security;
using THub.Domain.Security;
using THub.Web.Security;

namespace THub.Web.Tests;

public sealed class AccessControlEvaluatorTests
{
    [Fact]
    public async Task BootstrapSystemAdministrator_HasEveryResourcePermissionWithoutStoreLookup()
    {
        var store = new StubAccessControlStore { ThrowOnLoad = true };
        var evaluator = CreateEvaluator(
            store,
            new AuthorizationBootstrapOptions
            {
                SystemAdministratorUsers = ["CONTOSO\\admin"],
            });

        var allowed = await evaluator.HasPermissionAsync(
            CreatePrincipal("CONTOSO\\admin"),
            SecurityPermissions.WorkflowPublish,
            AccessResourceKind.Workflow,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(allowed);
    }

    [Fact]
    public async Task CustomRole_GrantsOnlyTheSelectedWorkflow()
    {
        var roleId = Guid.NewGuid();
        var allowedWorkflow = Guid.NewGuid();
        var role = new AccessRoleDto(
            roleId,
            "Finance",
            "Finance workflow access",
            null,
            [],
            [new AccessRoleAssignmentDto(
                Guid.NewGuid(),
                AccessPrincipalKind.User,
                "CONTOSO\\alice")],
            [new AccessResourceGrantDto(
                Guid.NewGuid(),
                AccessResourceKind.Workflow,
                allowedWorkflow,
                SecurityPermissions.WorkflowView)]);
        var evaluator = CreateEvaluator(
            new StubAccessControlStore
            {
                Snapshot = new AccessControlSnapshot([role]),
            },
            new AuthorizationBootstrapOptions());
        var principal = CreatePrincipal("CONTOSO\\alice");

        Assert.True(await evaluator.HasPermissionAsync(
            principal,
            SecurityPermissions.WorkflowView,
            AccessResourceKind.Workflow,
            allowedWorkflow,
            CancellationToken.None));
        Assert.False(await evaluator.HasPermissionAsync(
            principal,
            SecurityPermissions.WorkflowView,
            AccessResourceKind.Workflow,
            Guid.NewGuid(),
            CancellationToken.None));
    }

    [Fact]
    public async Task WindowsGroupAssignment_ReceivesRolePermissions()
    {
        var role = new AccessRoleDto(
            Guid.NewGuid(),
            "Operators",
            "Scheduled operations",
            null,
            [SecurityPermissions.ScheduleManage],
            [new AccessRoleAssignmentDto(
                Guid.NewGuid(),
                AccessPrincipalKind.WindowsGroup,
                "CONTOSO\\THub Operators")],
            []);
        var evaluator = CreateEvaluator(
            new StubAccessControlStore
            {
                Snapshot = new AccessControlSnapshot([role]),
            },
            new AuthorizationBootstrapOptions());

        Assert.True(await evaluator.HasPermissionAsync(
            CreatePrincipal("CONTOSO\\bob", "CONTOSO\\THub Operators"),
            SecurityPermissions.ScheduleManage,
            cancellationToken: CancellationToken.None));
    }

    private static AccessControlEvaluator CreateEvaluator(
        IAccessControlStore store,
        AuthorizationBootstrapOptions options) =>
        new(store, Options.Create(options));

    private static ClaimsPrincipal CreatePrincipal(string name, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, name) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            "test",
            ClaimTypes.Name,
            ClaimTypes.Role));
    }

    private sealed class StubAccessControlStore : IAccessControlStore
    {
        public AccessControlSnapshot Snapshot { get; init; } = new([]);
        public bool ThrowOnLoad { get; init; }

        public Task<AccessControlSnapshot> LoadAsync(CancellationToken cancellationToken) =>
            ThrowOnLoad
                ? throw new InvalidOperationException("Store should not be read.")
                : Task.FromResult(Snapshot);

        public Task<AccessRoleWriteStatus> SaveCustomRoleAsync(
            AccessRole role,
            IReadOnlyList<AccessRolePermission> permissions,
            IReadOnlyList<AccessRoleAssignment> assignments,
            IReadOnlyList<AccessResourceGrant> resourceGrants,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AccessRoleWriteStatus> DeleteCustomRoleAsync(
            Guid roleId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AccessRoleWriteStatus> ReplaceAssignmentsAsync(
            Guid roleId,
            IReadOnlyList<AccessRoleAssignment> assignments,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
