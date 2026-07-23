using THub.Application.Actions;
using THub.Application.Connections;
using THub.Domain.Actions;
using THub.Domain.Workflows;

namespace THub.Application.Tests;

public sealed class TrustedActionWorkflowAuthorizationTests
{
    [Fact]
    public async Task PublishRequiresTheReferencedActionToBeAvailableToAnEffectiveRole()
    {
        var actionId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var action = new TrustedAction(
            actionId,
            "Approved webhook",
            TrustedActionKind.Webhook,
            "{}",
            credentialReference: null,
            "administrator",
            DateTimeOffset.UtcNow);
        var store = new FakeTrustedActionStore(action, roleId);
        var authorization = new TrustedActionWorkflowAuthorization(store);
        var graph = new WorkflowGraph(
            [
                new(
                    "webhook",
                    WorkflowNodeKind.Webhook,
                    "Webhook",
                    0,
                    0,
                    $$"""{"trustedActionId":"{{actionId:D}}","body":"{}"}"""),
            ],
            []);

        var denied = await authorization.ValidatePublishAsync(
            graph,
            new HashSet<Guid>(),
            CancellationToken.None);
        var allowed = await authorization.ValidatePublishAsync(
            graph,
            new HashSet<Guid> { roleId },
            CancellationToken.None);

        Assert.Contains(denied, issue => issue.Code == "node.trusted-action.unauthorized");
        Assert.Empty(allowed);
    }

    private sealed class FakeTrustedActionStore(
        TrustedAction action,
        Guid permittedRoleId) : ITrustedActionStore
    {
        public Task<IReadOnlyList<TrustedAction>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TrustedAction>>([action]);

        public Task<IReadOnlyList<TrustedAction>> ListAvailableAsync(
            IReadOnlySet<Guid> roleIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TrustedAction>>(
                roleIds.Contains(permittedRoleId) ? [action] : []);

        public Task<TrustedAction?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<TrustedAction?>(id == action.Id ? action : null);

        public Task<bool> CredentialExistsAsync(
            string credentialReference,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<TrustedActionWriteStatus> AddAsync(
            TrustedAction candidate,
            ConnectionCredentialWrite? credentialWrite,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TrustedActionWriteStatus> SaveAsync(
            TrustedAction candidate,
            DateTimeOffset expectedUpdatedAtUtc,
            ConnectionCredentialWrite? credentialWrite,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
