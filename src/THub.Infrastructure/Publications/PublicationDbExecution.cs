using System.Data;
using Microsoft.EntityFrameworkCore;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Publications;

internal static class PublicationDbExecution
{
    public static async Task<T> InTransactionAsync<T>(
        IDbContextFactory<THubDbContext> contextFactory,
        Func<THubDbContext, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(operation);

        await using var strategyContext =
            await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var db =
                await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await db.Database
                .BeginTransactionAsync(isolationLevel, cancellationToken)
                .ConfigureAwait(false);
            var result = await operation(db, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }).ConfigureAwait(false);
    }
}
