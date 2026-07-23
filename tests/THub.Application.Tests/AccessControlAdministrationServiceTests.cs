using THub.Application.Security;
using THub.Domain.Security;

namespace THub.Application.Tests;

public sealed class AccessControlAdministrationServiceTests
{
    [Fact]
    public async Task SaveAsync_PersistsCustomRoleAssignmentsAndResourceGrants()
    {
        var store = new RecordingAccessControlStore();
        var service = new AccessControlAdministrationService(
            store,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 23, 1, 2, 3, TimeSpan.Zero)));
        var workflowId = Guid.NewGuid();

        var status = await service.SaveAsync(
            new SaveAccessRoleCommand(
                null,
                "Finance publishers",
                "Publishes one governed workflow.",
                [SecurityPermissions.WorkflowView],
                [new SaveAccessRoleAssignmentCommand(
                    AccessPrincipalKind.WindowsGroup,
                    "CONTOSO\\Finance Developers")],
                [new SaveAccessResourceGrantCommand(
                    AccessResourceKind.Workflow,
                    workflowId,
                    SecurityPermissions.WorkflowPublish)],
                "CONTOSO\\admin"),
            CancellationToken.None);

        Assert.Equal(AccessRoleWriteStatus.Saved, status);
        Assert.Equal("Finance publishers", store.Role!.Name);
        Assert.Equal("CONTOSO\\FINANCE DEVELOPERS", Assert.Single(store.Assignments).NormalizedPrincipalName);
        Assert.Equal(workflowId, Assert.Single(store.Grants).ResourceId);
    }

    [Fact]
    public async Task SaveAsync_RejectsPermissionOutsideResourceCatalog()
    {
        var service = new AccessControlAdministrationService(
            new RecordingAccessControlStore(),
            TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(
            new SaveAccessRoleCommand(
                null,
                "Invalid",
                "Invalid grant",
                [],
                [],
                [new SaveAccessResourceGrantCommand(
                    AccessResourceKind.Connection,
                    Guid.NewGuid(),
                    SecurityPermissions.WorkflowPublish)],
                "test"),
            CancellationToken.None));
    }

    private sealed class RecordingAccessControlStore : IAccessControlStore
    {
        public AccessRole? Role { get; private set; }
        public IReadOnlyList<AccessRoleAssignment> Assignments { get; private set; } = [];
        public IReadOnlyList<AccessResourceGrant> Grants { get; private set; } = [];

        public Task<AccessControlSnapshot> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AccessControlSnapshot([]));

        public Task<AccessRoleWriteStatus> SaveCustomRoleAsync(
            AccessRole role,
            IReadOnlyList<AccessRolePermission> permissions,
            IReadOnlyList<AccessRoleAssignment> assignments,
            IReadOnlyList<AccessResourceGrant> resourceGrants,
            CancellationToken cancellationToken)
        {
            Role = role;
            Assignments = assignments;
            Grants = resourceGrants;
            return Task.FromResult(AccessRoleWriteStatus.Saved);
        }

        public Task<AccessRoleWriteStatus> DeleteCustomRoleAsync(
            Guid roleId,
            CancellationToken cancellationToken) =>
            Task.FromResult(AccessRoleWriteStatus.Saved);

        public Task<AccessRoleWriteStatus> ReplaceAssignmentsAsync(
            Guid roleId,
            IReadOnlyList<AccessRoleAssignment> assignments,
            CancellationToken cancellationToken) =>
            Task.FromResult(AccessRoleWriteStatus.Saved);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
