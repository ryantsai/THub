using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using THub.Application.Scheduling;
using THub.Infrastructure.Persistence;
using THub.Infrastructure.Scheduling;

namespace THub.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("THub")
            ?? throw new InvalidOperationException("Connection string 'THub' is not configured.");

        services.AddPooledDbContextFactory<THubDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
                sql.EnableRetryOnFailure(maxRetryCount: 5)));

        services.AddSingleton<ISchedulerCoordinator, SqlSchedulerCoordinator>();
        return services;
    }
}
