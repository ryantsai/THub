using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using THub.Application.Connections;
using THub.Domain.Connections;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Connections;

internal sealed class SqlDataConnectionStore(
    IDbContextFactory<THubDbContext> contextFactory,
    ConnectionCredentialProtector credentialProtector) : IDataConnectionStore
{
    public async Task<IReadOnlyList<DataConnection>> ListAsync(
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Connections
            .AsNoTracking()
            .OrderBy(connection => connection.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<DataConnection?> FindAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Connections
            .AsNoTracking()
            .SingleOrDefaultAsync(connection => connection.Id == id, cancellationToken);
    }

    public async Task<bool> CredentialExistsAsync(
        string secretReference,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.EncryptedConnectionCredentials
            .AnyAsync(
                credential => credential.SecretReference == secretReference,
                cancellationToken);
    }

    public async Task<ConnectionSaveStatus> AddAsync(
        DataConnection connection,
        CancellationToken cancellationToken) =>
        await AddAsync(connection, credentialWrite: null, cancellationToken);

    public async Task<ConnectionSaveStatus> AddAsync(
        DataConnection connection,
        ConnectionCredentialWrite? credentialWrite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Connections.Add(connection);
        if (credentialWrite is not null)
        {
            await UpsertCredentialAsync(db, credentialWrite, cancellationToken);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return ConnectionSaveStatus.Saved;
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            return ConnectionSaveStatus.DuplicateName;
        }
    }

    public async Task<ConnectionSaveStatus> SaveAsync(
        DataConnection connection,
        DateTimeOffset expectedUpdatedAtUtc,
        CancellationToken cancellationToken) =>
        await SaveAsync(
            connection,
            expectedUpdatedAtUtc,
            credentialWrite: null,
            cancellationToken);

    public async Task<ConnectionSaveStatus> SaveAsync(
        DataConnection connection,
        DateTimeOffset expectedUpdatedAtUtc,
        ConnectionCredentialWrite? credentialWrite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var current = await db.Connections.SingleOrDefaultAsync(
            candidate => candidate.Id == connection.Id,
            cancellationToken);
        if (current is null)
        {
            return ConnectionSaveStatus.NotFound;
        }
        if (current.UpdatedAtUtc != expectedUpdatedAtUtc.ToUniversalTime())
        {
            return ConnectionSaveStatus.Conflict;
        }

        db.Entry(current).CurrentValues.SetValues(connection);
        if (credentialWrite is not null)
        {
            await UpsertCredentialAsync(db, credentialWrite, cancellationToken);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return ConnectionSaveStatus.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConnectionSaveStatus.Conflict;
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            return ConnectionSaveStatus.DuplicateName;
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };

    private async Task UpsertCredentialAsync(
        THubDbContext db,
        ConnectionCredentialWrite write,
        CancellationToken cancellationToken)
    {
        var replacement = credentialProtector.Protect(write);
        var current = await db.EncryptedConnectionCredentials
            .SingleOrDefaultAsync(
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
}
