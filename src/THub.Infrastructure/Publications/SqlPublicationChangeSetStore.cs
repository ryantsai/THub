using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using THub.Application.Publications;
using THub.Domain.Publications;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Publications;

public sealed class SqlPublicationChangeSetStore(
    IDbContextFactory<THubDbContext> contextFactory) : IPublicationChangeSetStore
{
    public async Task<PublicationChangeSet?> FindAsync(
        Guid publicationId,
        Guid changeSetId,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await db.PublicationChangeSets
            .AsNoTracking()
            .Include("_changes")
            .AsSplitQuery()
            .SingleOrDefaultAsync(
                changeSet => changeSet.PublicationId == publicationId &&
                    changeSet.Id == changeSetId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PublicationChangeSetWriteStatus> AddAsync(
        PublicationChangeSet changeSet,
        string expectedGrantFingerprint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedGrantFingerprint);
        try
        {
            return await PublicationDbExecution.InTransactionAsync(
                contextFactory,
                async (db, token) =>
                {
                    if (!await GrantSnapshotIsCurrentAsync(
                            db,
                            changeSet.PublicationId,
                            changeSet.PublicationVersionId,
                            expectedGrantFingerprint,
                            token).ConfigureAwait(false))
                    {
                        return PublicationChangeSetWriteStatus.Conflict;
                    }

                    db.PublicationChangeSets.Add(changeSet);
                    await db.SaveChangesAsync(token).ConfigureAwait(false);
                    return PublicationChangeSetWriteStatus.Saved;
                },
                cancellationToken,
                IsolationLevel.Serializable).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return PublicationChangeSetWriteStatus.Conflict;
        }
        catch (DbUpdateConcurrencyException)
        {
            return PublicationChangeSetWriteStatus.Conflict;
        }
    }

    public async Task<PublicationChangeSetWriteStatus> UpdateAsync(
        PublicationChangeSet changeSet,
        DateTimeOffset expectedUpdatedAtUtc,
        string expectedGrantFingerprint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedGrantFingerprint);
        try
        {
            return await PublicationDbExecution.InTransactionAsync(
                contextFactory,
                async (db, token) =>
                {
                    if (!await GrantSnapshotIsCurrentAsync(
                            db,
                            changeSet.PublicationId,
                            changeSet.PublicationVersionId,
                            expectedGrantFingerprint,
                            token).ConfigureAwait(false))
                    {
                        return PublicationChangeSetWriteStatus.Conflict;
                    }

                    var current = await db.PublicationChangeSets
                        .SingleOrDefaultAsync(candidate => candidate.Id == changeSet.Id &&
                            candidate.PublicationId == changeSet.PublicationId, token)
                        .ConfigureAwait(false);
                    if (current is null)
                    {
                        return PublicationChangeSetWriteStatus.NotFound;
                    }

                    if (current.UpdatedAtUtc != expectedUpdatedAtUtc.ToUniversalTime())
                    {
                        return PublicationChangeSetWriteStatus.Conflict;
                    }

                    db.Entry(current).CurrentValues.SetValues(changeSet);
                    await db.SaveChangesAsync(token).ConfigureAwait(false);
                    return PublicationChangeSetWriteStatus.Saved;
                },
                cancellationToken,
                IsolationLevel.Serializable).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return PublicationChangeSetWriteStatus.Conflict;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };

    private static async Task<bool> GrantSnapshotIsCurrentAsync(
        THubDbContext db,
        Guid publicationId,
        Guid publicationVersionId,
        string expectedGrantFingerprint,
        CancellationToken cancellationToken)
    {
        var publicationIsCurrent = await db.Publications
            .AsNoTracking()
            .AnyAsync(publication =>
                publication.Id == publicationId &&
                publication.Kind == PublicationKind.Editor &&
                publication.State == PublicationState.Active &&
                publication.ActiveVersionId == publicationVersionId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!publicationIsCurrent)
        {
            return false;
        }

        var grants = await db.PublicationGrants
            .AsNoTracking()
            .Where(grant => grant.PublicationId == publicationId)
            .OrderBy(grant => grant.RoleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return string.Equals(
            PublicationGrantFingerprint.Compute(grants),
            expectedGrantFingerprint,
            StringComparison.Ordinal);
    }
}
