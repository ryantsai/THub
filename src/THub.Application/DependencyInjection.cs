using Microsoft.Extensions.DependencyInjection;
using THub.Application.Scheduling;
using THub.Application.Workflows;

namespace THub.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<WorkflowGraphValidator>();
        services.AddSingleton<ScheduleCalculator>();
        return services;
    }
}
