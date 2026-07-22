using Microsoft.EntityFrameworkCore;

namespace THub.Infrastructure.Persistence;

internal static class THubDbExecution
{
    public static async Task<T> ExecuteAsync<T>(
        IDbContextFactory<THubDbContext> contextFactory,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(operation);

        await using var strategyContext =
            await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            () => operation(cancellationToken)).ConfigureAwait(false);
    }
}
