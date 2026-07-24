using Microsoft.EntityFrameworkCore;
using THub.Application.Publications;
using THub.Domain.Connections;
using THub.Domain.Publications;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Publications;

public sealed class PublicationSourceDataReader(
    IDbContextFactory<THubDbContext> contextFactory,
    SqlPublicationSourceDataReader sqlServer,
    RelationalPublicationSourceDataReader relational)
    : IPublicationSourceDataReader
{
    public async Task<PublicationSourceReadResult<PublicationSourceRowPage>> ReadRowsAsync(
        PublicationVersion version,
        PublicationSourceReadQuery query,
        CancellationToken cancellationToken) =>
        await ReadAsync(
            version,
            token => sqlServer.ReadRowsAsync(version, query, token),
            token => relational.ReadRowsAsync(version, query, token),
            () => new(PublicationSourceReadStatus.Unavailable, null),
            cancellationToken).ConfigureAwait(false);

    public async Task<PublicationSourceReadResult<PublicationSourceLookupPage>> ReadForeignKeyLookupAsync(
        PublicationVersion version,
        PublicationColumn column,
        PublicationForeignKeySourceQuery query,
        CancellationToken cancellationToken) =>
        await ReadAsync(
            version,
            token => sqlServer.ReadForeignKeyLookupAsync(version, column, query, token),
            token => relational.ReadForeignKeyLookupAsync(version, column, query, token),
            () => new(PublicationSourceReadStatus.Unavailable, null),
            cancellationToken).ConfigureAwait(false);

    public async Task<PublicationSourceReadResult<PublicationSourceForeignKeyResolution>> ResolveForeignKeysAsync(
        PublicationVersion version,
        IReadOnlyList<PublicationForeignKeyTuple> tuples,
        CancellationToken cancellationToken) =>
        await ReadAsync(
            version,
            token => sqlServer.ResolveForeignKeysAsync(version, tuples, token),
            token => relational.ResolveForeignKeysAsync(version, tuples, token),
            () => new(PublicationSourceReadStatus.Unavailable, null),
            cancellationToken).ConfigureAwait(false);

    private async Task<T> ReadAsync<T>(
        PublicationVersion version,
        Func<CancellationToken, Task<T>> sqlServerRead,
        Func<CancellationToken, Task<T>> relationalRead,
        Func<T> unsupported,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var kind = await db.Connections
            .AsNoTracking()
            .Where(connection => connection.Id == version.ConnectionId && connection.IsEnabled)
            .Select(connection => (ConnectionKind?)connection.Kind)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return kind switch
        {
            ConnectionKind.SqlServer => await sqlServerRead(cancellationToken).ConfigureAwait(false),
            ConnectionKind.MySql or ConnectionKind.PostgreSql or ConnectionKind.Oracle =>
                await relationalRead(cancellationToken).ConfigureAwait(false),
            _ => unsupported()
        };
    }
}
