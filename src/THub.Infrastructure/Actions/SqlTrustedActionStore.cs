using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using THub.Application.Actions;
using THub.Application.Connections;
using THub.Domain.Actions;
using THub.Domain.Security;
using THub.Infrastructure.Connections;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Actions;

internal sealed class SqlTrustedActionStore(
    IDbContextFactory<THubDbContext> contextFactory,
    ConnectionCredentialProtector credentialProtector) : ITrustedActionStore
{
    public async Task<IReadOnlyList<TrustedAction>> ListAsync(
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.TrustedActions.AsNoTracking()
            .OrderBy(action => action.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TrustedAction>> ListAvailableAsync(
        IReadOnlySet<Guid> roleIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roleIds);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.TrustedActions.AsNoTracking().Where(action => action.IsEnabled);
        if (!roleIds.Contains(SystemRoleIds.SystemAdministrator))
        {
            var ids = roleIds.ToArray();
            query = query.Where(action => db.AccessResourceGrants.Any(grant =>
                ids.Contains(grant.RoleId) &&
                grant.ResourceKind == AccessResourceKind.TrustedAction &&
                grant.ResourceId == action.Id &&
                grant.Permission == THub.Application.Security.SecurityPermissions.TrustedActionUse));
        }

        return await query.OrderBy(action => action.Name).ToListAsync(cancellationToken);
    }

    public async Task<TrustedAction?> FindAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.TrustedActions.AsNoTracking()
            .SingleOrDefaultAsync(action => action.Id == id, cancellationToken);
    }

    public async Task<bool> CredentialExistsAsync(
        string credentialReference,
        CancellationToken cancellationToken)
    {
        var storageReference =
            TrustedActionCredentialReferences.ToStorageReference(credentialReference);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.EncryptedConnectionCredentials.AnyAsync(
            credential => credential.SecretReference == storageReference,
            cancellationToken);
    }

    public async Task<TrustedActionWriteStatus> AddAsync(
        TrustedAction action,
        ConnectionCredentialWrite? credentialWrite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.TrustedActions.Add(action);
        if (credentialWrite is not null)
        {
            await UpsertCredentialAsync(db, credentialWrite, cancellationToken);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return TrustedActionWriteStatus.Saved;
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            return TrustedActionWriteStatus.DuplicateName;
        }
    }

    public async Task<TrustedActionWriteStatus> SaveAsync(
        TrustedAction action,
        DateTimeOffset expectedUpdatedAtUtc,
        ConnectionCredentialWrite? credentialWrite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var current = await db.TrustedActions.SingleOrDefaultAsync(
            candidate => candidate.Id == action.Id,
            cancellationToken);
        if (current is null)
        {
            return TrustedActionWriteStatus.NotFound;
        }

        if (current.UpdatedAtUtc != expectedUpdatedAtUtc.ToUniversalTime())
        {
            return TrustedActionWriteStatus.Conflict;
        }

        db.Entry(current).CurrentValues.SetValues(action);
        if (credentialWrite is not null)
        {
            await UpsertCredentialAsync(db, credentialWrite, cancellationToken);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return TrustedActionWriteStatus.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            return TrustedActionWriteStatus.Conflict;
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            return TrustedActionWriteStatus.DuplicateName;
        }
    }

    private async Task UpsertCredentialAsync(
        THubDbContext db,
        ConnectionCredentialWrite write,
        CancellationToken cancellationToken)
    {
        var replacement = credentialProtector.Protect(write);
        var current = await db.EncryptedConnectionCredentials.SingleOrDefaultAsync(
            credential => credential.SecretReference == write.SecretReference,
            cancellationToken);
        if (current is null)
        {
            db.EncryptedConnectionCredentials.Add(replacement);
        }
        else
        {
            current.ReplaceWith(replacement);
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };
}
