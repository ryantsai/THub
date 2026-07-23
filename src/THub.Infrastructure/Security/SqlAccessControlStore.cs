using System.Data;
using Microsoft.EntityFrameworkCore;
using THub.Application.Security;
using THub.Domain.Security;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Security;

public sealed class SqlAccessControlStore(
    IDbContextFactory<THubDbContext> contextFactory) : IAccessControlStore
{
    public async Task<AccessControlSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var roles = await db.AccessRoles.AsNoTracking()
            .OrderByDescending(role => role.SystemRole != null)
            .ThenBy(role => role.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var permissions = await db.AccessRolePermissions.AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var assignments = await db.AccessRoleAssignments.AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var grants = await db.AccessResourceGrants.AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new AccessControlSnapshot(roles.Select(role => new AccessRoleDto(
            role.Id,
            role.Name,
            role.Description,
            role.SystemRole,
            permissions
                .Where(permission => permission.RoleId == role.Id)
                .Select(permission => permission.Permission)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            assignments
                .Where(assignment => assignment.RoleId == role.Id)
                .Select(assignment => new AccessRoleAssignmentDto(
                    assignment.Id,
                    assignment.PrincipalKind,
                    assignment.PrincipalName))
                .OrderBy(assignment => assignment.PrincipalKind)
                .ThenBy(assignment => assignment.PrincipalName, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            grants
                .Where(grant => grant.RoleId == role.Id)
                .Select(grant => new AccessResourceGrantDto(
                    grant.Id,
                    grant.ResourceKind,
                    grant.ResourceId,
                    grant.Permission))
                .OrderBy(grant => grant.ResourceKind)
                .ThenBy(grant => grant.ResourceId)
                .ThenBy(grant => grant.Permission, StringComparer.Ordinal)
                .ToArray())).ToArray());
    }

    public async Task<AccessRoleWriteStatus> SaveCustomRoleAsync(
        AccessRole role,
        IReadOnlyList<AccessRolePermission> permissions,
        IReadOnlyList<AccessRoleAssignment> assignments,
        IReadOnlyList<AccessResourceGrant> resourceGrants,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        var workflowIds = resourceGrants
            .Where(grant => grant.ResourceKind == AccessResourceKind.Workflow)
            .Select(grant => grant.ResourceId)
            .Distinct()
            .ToArray();
        var existingWorkflowCount = await db.Workflows
            .CountAsync(workflow => workflowIds.Contains(workflow.Id), cancellationToken)
            .ConfigureAwait(false);
        if (existingWorkflowCount != workflowIds.Length)
        {
            return AccessRoleWriteStatus.NotFound;
        }

        var connectionIds = resourceGrants
            .Where(grant => grant.ResourceKind == AccessResourceKind.Connection)
            .Select(grant => grant.ResourceId)
            .Distinct()
            .ToArray();
        var existingConnectionCount = await db.Connections
            .CountAsync(connection => connectionIds.Contains(connection.Id), cancellationToken)
            .ConfigureAwait(false);
        if (existingConnectionCount != connectionIds.Length)
        {
            return AccessRoleWriteStatus.NotFound;
        }

        var existing = await db.AccessRoles
            .SingleOrDefaultAsync(item => item.Id == role.Id, cancellationToken)
            .ConfigureAwait(false);
        if (existing?.SystemRole is not null)
        {
            return AccessRoleWriteStatus.SystemRoleImmutable;
        }

        if (existing is null)
        {
            db.AccessRoles.Add(role);
        }
        else
        {
            existing.Rename(role.Name, role.Description);
        }

        var oldPermissions = await db.AccessRolePermissions
            .Where(item => item.RoleId == role.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var oldAssignments = await db.AccessRoleAssignments
            .Where(item => item.RoleId == role.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var oldGrants = await db.AccessResourceGrants
            .Where(item => item.RoleId == role.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        db.RemoveRange(oldPermissions);
        db.RemoveRange(oldAssignments);
        db.RemoveRange(oldGrants);
        db.AddRange(permissions);
        db.AddRange(assignments);
        db.AddRange(resourceGrants);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return AccessRoleWriteStatus.Saved;
        }
        catch (DbUpdateException)
        {
            return AccessRoleWriteStatus.Conflict;
        }
    }

    public async Task<AccessRoleWriteStatus> DeleteCustomRoleAsync(
        Guid roleId,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var role = await db.AccessRoles
            .SingleOrDefaultAsync(item => item.Id == roleId, cancellationToken)
            .ConfigureAwait(false);
        if (role is null)
        {
            return AccessRoleWriteStatus.NotFound;
        }

        if (role.SystemRole is not null)
        {
            return AccessRoleWriteStatus.SystemRoleImmutable;
        }

        db.AccessRoles.Remove(role);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return AccessRoleWriteStatus.Saved;
    }

    public async Task<AccessRoleWriteStatus> ReplaceAssignmentsAsync(
        Guid roleId,
        IReadOnlyList<AccessRoleAssignment> assignments,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await db.AccessRoles.AnyAsync(role => role.Id == roleId, cancellationToken)
            .ConfigureAwait(false))
        {
            return AccessRoleWriteStatus.NotFound;
        }

        var current = await db.AccessRoleAssignments
            .Where(assignment => assignment.RoleId == roleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        db.AccessRoleAssignments.RemoveRange(current);
        db.AccessRoleAssignments.AddRange(assignments);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return AccessRoleWriteStatus.Saved;
        }
        catch (DbUpdateException)
        {
            return AccessRoleWriteStatus.Conflict;
        }
    }
}
