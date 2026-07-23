using THub.Domain.Security;

namespace THub.Application.Security;

public sealed class AccessControlAdministrationService(
    IAccessControlStore store,
    TimeProvider timeProvider)
{
    private const int MaximumCustomRoles = 128;
    private const int MaximumAssignmentsPerRole = 128;
    private const int MaximumGrantsPerRole = 512;

    private readonly IAccessControlStore _store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public Task<AccessControlSnapshot> ListAsync(CancellationToken cancellationToken) =>
        _store.LoadAsync(cancellationToken);

    public async Task<AccessRoleWriteStatus> SaveAsync(
        SaveAccessRoleCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.RoleId == Guid.Empty ||
            command.Permissions is null ||
            command.Assignments is null ||
            command.ResourceGrants is null ||
            command.Assignments.Count > MaximumAssignmentsPerRole ||
            command.ResourceGrants.Count > MaximumGrantsPerRole ||
            command.Permissions.Count > SecurityPermissions.GlobalAssignable.Count ||
            command.Permissions.Any(permission => !SecurityPermissions.GlobalAssignable.Contains(permission)) ||
            command.Permissions.Distinct(StringComparer.Ordinal).Count() != command.Permissions.Count ||
            command.Assignments.Any(assignment =>
                assignment is null ||
                !Enum.IsDefined(assignment.PrincipalKind) ||
                string.IsNullOrWhiteSpace(assignment.PrincipalName)) ||
            command.Assignments
                .Select(assignment => (
                    assignment.PrincipalKind,
                    assignment.PrincipalName.Trim().ToUpperInvariant()))
                .Distinct()
                .Count() != command.Assignments.Count ||
            command.ResourceGrants.Any(grant =>
                grant is null ||
                grant.ResourceId == Guid.Empty ||
                !Enum.IsDefined(grant.ResourceKind) ||
                !SecurityPermissions.ResourcePermissions[grant.ResourceKind].Contains(grant.Permission)) ||
            command.ResourceGrants
                .Select(grant => (grant.ResourceKind, grant.ResourceId, grant.Permission))
                .Distinct()
                .Count() != command.ResourceGrants.Count)
        {
            throw new ArgumentException("The custom role definition is invalid.", nameof(command));
        }

        var current = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (command.RoleId is null &&
            current.Roles.Count(role => role.SystemRole is null) >= MaximumCustomRoles)
        {
            throw new InvalidOperationException("The maximum number of custom roles has been reached.");
        }

        var existing = command.RoleId is null
            ? null
            : current.Roles.SingleOrDefault(role => role.Id == command.RoleId.Value);
        if (existing?.SystemRole is not null)
        {
            return AccessRoleWriteStatus.SystemRoleImmutable;
        }

        var roleId = command.RoleId ?? Guid.NewGuid();
        var role = new AccessRole(
            roleId,
            command.Name,
            command.Description,
            null,
            existing is null ? _timeProvider.GetUtcNow() : DateTimeOffset.UnixEpoch,
            command.Actor);
        var permissions = command.Permissions
            .Select(permission => new AccessRolePermission(Guid.NewGuid(), roleId, permission))
            .ToArray();
        var assignments = command.Assignments
            .Select(assignment => new AccessRoleAssignment(
                Guid.NewGuid(),
                roleId,
                assignment.PrincipalKind,
                assignment.PrincipalName))
            .ToArray();
        var grants = command.ResourceGrants
            .Select(grant => new AccessResourceGrant(
                Guid.NewGuid(),
                roleId,
                grant.ResourceKind,
                grant.ResourceId,
                grant.Permission))
            .ToArray();

        return await _store.SaveCustomRoleAsync(
                role,
                permissions,
                assignments,
                grants,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<AccessRoleWriteStatus> DeleteAsync(
        Guid roleId,
        CancellationToken cancellationToken) =>
        roleId == Guid.Empty
            ? throw new ArgumentException("A role identifier is required.", nameof(roleId))
            : _store.DeleteCustomRoleAsync(roleId, cancellationToken);

    public async Task<AccessRoleWriteStatus> ReplaceAssignmentsAsync(
        Guid roleId,
        IReadOnlyList<SaveAccessRoleAssignmentCommand> assignments,
        CancellationToken cancellationToken)
    {
        if (roleId == Guid.Empty ||
            assignments is null ||
            assignments.Count > MaximumAssignmentsPerRole ||
            assignments.Any(assignment =>
                assignment is null ||
                !Enum.IsDefined(assignment.PrincipalKind) ||
                string.IsNullOrWhiteSpace(assignment.PrincipalName)) ||
            assignments.Select(assignment => (
                    assignment.PrincipalKind,
                    assignment.PrincipalName.Trim().ToUpperInvariant()))
                .Distinct()
                .Count() != assignments.Count)
        {
            throw new ArgumentException("The role assignments are invalid.", nameof(assignments));
        }

        var snapshot = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.Roles.All(role => role.Id != roleId))
        {
            return AccessRoleWriteStatus.NotFound;
        }

        return await _store.ReplaceAssignmentsAsync(
                roleId,
                assignments.Select(assignment => new AccessRoleAssignment(
                    Guid.NewGuid(),
                    roleId,
                    assignment.PrincipalKind,
                    assignment.PrincipalName)).ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
