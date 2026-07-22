using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using THub.Application.Publications;
using THub.Domain.Publications;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Publications;

public sealed class SqlPublicationGrantManagementStore(
    IDbContextFactory<THubDbContext> contextFactory,
    TimeProvider timeProvider) : IPublicationGrantManagementStore
{
    public async Task<PublicationGrantWriteStatus> ReplaceAsync(
        Guid publicationId,
        string expectedFingerprint,
        IReadOnlyList<PublicationGrant> grants,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFingerprint);
        ArgumentNullException.ThrowIfNull(grants);
        if (grants.Any(grant => grant.PublicationId != publicationId) ||
            grants.Select(grant => grant.Role).Distinct().Count() != grants.Count)
        {
            return PublicationGrantWriteStatus.Conflict;
        }

        await using var strategyContext = await contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                await using var db = await contextFactory
                    .CreateDbContextAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using var transaction = await db.Database
                    .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                    .ConfigureAwait(false);
                var publication = await db.Publications
                    .AsNoTracking()
                    .SingleOrDefaultAsync(candidate => candidate.Id == publicationId, cancellationToken)
                    .ConfigureAwait(false);
                if (publication is null)
                {
                    return PublicationGrantWriteStatus.NotFound;
                }

                if (publication.Kind != PublicationKind.Editor ||
                    publication.State == PublicationState.Archived)
                {
                    return PublicationGrantWriteStatus.Conflict;
                }

                var current = await db.PublicationGrants
                    .Where(grant => grant.PublicationId == publicationId)
                    .OrderBy(grant => grant.Role)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                var currentFingerprint = PublicationGrantFingerprint.Compute(current);
                var desiredFingerprint = PublicationGrantFingerprint.Compute(grants);
                if (string.Equals(currentFingerprint, desiredFingerprint, StringComparison.Ordinal))
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return PublicationGrantWriteStatus.Saved;
                }

                if (!string.Equals(currentFingerprint, expectedFingerprint, StringComparison.Ordinal))
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return PublicationGrantWriteStatus.Conflict;
                }

                var invalidatedAtUtc = timeProvider.GetUtcNow().ToUniversalTime();
                await db.PublicationChangeSets
                    .Where(changeSet =>
                        changeSet.PublicationId == publicationId &&
                        (changeSet.Status == PublicationChangeSetStatus.Pending ||
                            changeSet.Status == PublicationChangeSetStatus.Approved))
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(
                                changeSet => changeSet.Status,
                                PublicationChangeSetStatus.Rejected)
                            .SetProperty(
                                changeSet => changeSet.CompletedAtUtc,
                                changeSet => changeSet.UpdatedAtUtc > invalidatedAtUtc
                                    ? changeSet.UpdatedAtUtc
                                    : invalidatedAtUtc)
                            .SetProperty(
                                changeSet => changeSet.OutcomeDetail,
                                PublicationChangeSet.AuthorizationChangedOutcome)
                            .SetProperty(
                                changeSet => changeSet.UpdatedAtUtc,
                                changeSet => changeSet.UpdatedAtUtc > invalidatedAtUtc
                                    ? changeSet.UpdatedAtUtc
                                    : invalidatedAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false);

                db.PublicationGrants.RemoveRange(current);
                db.PublicationGrants.AddRange(grants);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return PublicationGrantWriteStatus.Saved;
            }).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return PublicationGrantWriteStatus.Conflict;
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            return PublicationGrantWriteStatus.Conflict;
        }
    }
}
