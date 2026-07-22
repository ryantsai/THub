using Microsoft.Extensions.Options;
using THub.Application.Scheduling;

namespace THub.Worker;

public sealed class Worker(
    ISchedulerCoordinator scheduler,
    IOptions<SchedulerOptions> options,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("THub scheduler worker started on {MachineName}.", Environment.MachineName);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
            try
            {
                var queued = await scheduler.EnqueueDueWorkflowsAsync(
                    DateTimeOffset.UtcNow,
                    stoppingToken);

                if (queued > 0)
                {
                    logger.LogInformation("Queued {WorkflowCount} due workflow runs.", queued);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Scheduler tick failed; it will retry.");
                delay = TimeSpan.FromSeconds(options.Value.ErrorRetrySeconds);
            }

            await Task.Delay(delay, stoppingToken);
        }

        logger.LogInformation("THub scheduler worker stopped.");
    }
}
