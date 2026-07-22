using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using THub.Application.Connections;
using THub.Domain.Connections;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Connections;

public sealed class SqlDataConnectionStore(
    IDbContextFactory<THubDbContext> contextFactory) : IDataConnectionStore
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

    public async Task<ConnectionSaveStatus> AddAsync(
        DataConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Connections.Add(connection);
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
}
