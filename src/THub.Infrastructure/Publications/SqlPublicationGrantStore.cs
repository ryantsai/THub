using Microsoft.EntityFrameworkCore;
using THub.Application.Publications;
using THub.Domain.Publications;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Publications;

public sealed class SqlPublicationGrantStore(
    IDbContextFactory<THubDbContext> contextFactory) : IPublicationGrantStore
{
    public async Task<IReadOnlyList<PublicationGrant>> ListAsync(
        Guid publicationId,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await db.PublicationGrants
            .AsNoTracking()
            .Where(grant => grant.PublicationId == publicationId)
            .OrderBy(grant => grant.RoleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
