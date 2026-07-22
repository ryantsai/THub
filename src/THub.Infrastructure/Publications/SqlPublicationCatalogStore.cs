using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using THub.Application.Publications;
using THub.Domain.Publications;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Publications;

public sealed class SqlPublicationCatalogStore(
    IDbContextFactory<THubDbContext> contextFactory) : IPublicationCatalogStore
{
    public async Task<IReadOnlyList<Publication>> ListAsync(
        PublicationCatalogQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var publications = db.Publications.AsNoTracking();
        if (query.Kind is PublicationKind kind)
        {
            publications = publications.Where(publication => publication.Kind == kind);
        }

        if (query.State is PublicationState state)
        {
            publications = publications.Where(publication => publication.State == state);
        }

        return await publications
            .OrderBy(publication => publication.Name)
            .ThenBy(publication => publication.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Publication?> FindAsync(
        Guid publicationId,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await db.Publications
            .AsNoTracking()
            .SingleOrDefaultAsync(publication => publication.Id == publicationId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Publication?> FindBySlugAsync(
        string slug,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await db.Publications
            .AsNoTracking()
            .SingleOrDefaultAsync(publication => publication.Slug == slug, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PublicationVersion>> ListVersionsAsync(
        Guid publicationId,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await VersionQuery(db)
            .Where(version => version.PublicationId == publicationId)
            .OrderByDescending(version => version.VersionNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PublicationVersion?> FindVersionAsync(
        Guid publicationId,
        Guid versionId,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await VersionQuery(db)
            .SingleOrDefaultAsync(
                version => version.PublicationId == publicationId && version.Id == versionId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> GetNextVersionNumberAsync(
        Guid publicationId,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var current = await db.PublicationVersions
            .Where(version => version.PublicationId == publicationId)
            .Select(version => (int?)version.VersionNumber)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false);
        return checked((current ?? 0) + 1);
    }

    public async Task<PublicationCatalogWriteStatus> AddPublicationAsync(
        Publication publication,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publication);
        try
        {
            return await PublicationDbExecution.InTransactionAsync(
                contextFactory,
                async (db, token) =>
                {
                    db.Publications.Add(publication);
                    await db.SaveChangesAsync(token).ConfigureAwait(false);
                    return PublicationCatalogWriteStatus.Saved;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception, "IX_Publications_Slug"))
        {
            return PublicationCatalogWriteStatus.DuplicateSlug;
        }
        catch (DbUpdateConcurrencyException)
        {
            return PublicationCatalogWriteStatus.Conflict;
        }
    }

    public async Task<PublicationCatalogWriteStatus> AddVersionAsync(
        PublicationVersion version,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        try
        {
            return await PublicationDbExecution.InTransactionAsync(
                contextFactory,
                async (db, token) =>
                {
                    db.PublicationVersions.Add(version);
                    await db.SaveChangesAsync(token).ConfigureAwait(false);
                    return PublicationCatalogWriteStatus.Saved;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(
            exception,
            "IX_PublicationVersions_PublicationId_VersionNumber"))
        {
            return PublicationCatalogWriteStatus.DuplicateVersion;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return PublicationCatalogWriteStatus.Conflict;
        }
        catch (DbUpdateConcurrencyException)
        {
            return PublicationCatalogWriteStatus.Conflict;
        }
    }

    public async Task<PublicationCatalogWriteStatus> UpdatePublicationAsync(
        Publication publication,
        DateTimeOffset expectedUpdatedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publication);
        try
        {
            return await PublicationDbExecution.InTransactionAsync(
                contextFactory,
                async (db, token) =>
                {
                    var current = await db.Publications
                        .SingleOrDefaultAsync(candidate => candidate.Id == publication.Id, token)
                        .ConfigureAwait(false);
                    if (current is null ||
                        current.UpdatedAtUtc != expectedUpdatedAtUtc.ToUniversalTime())
                    {
                        return PublicationCatalogWriteStatus.Conflict;
                    }

                    db.Entry(current).CurrentValues.SetValues(publication);
                    await db.SaveChangesAsync(token).ConfigureAwait(false);
                    return PublicationCatalogWriteStatus.Saved;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception, "IX_Publications_Slug"))
        {
            return PublicationCatalogWriteStatus.DuplicateSlug;
        }
        catch (DbUpdateConcurrencyException)
        {
            return PublicationCatalogWriteStatus.Conflict;
        }
    }

    private static IQueryable<PublicationVersion> VersionQuery(THubDbContext db) =>
        db.PublicationVersions
            .AsNoTracking()
            .Include("_columns.ForeignKey")
            .AsSplitQuery();

    private static bool IsUniqueViolation(DbUpdateException exception, string? indexName = null) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 } sqlException &&
        (indexName is null || sqlException.Message.Contains(indexName, StringComparison.Ordinal));
}
