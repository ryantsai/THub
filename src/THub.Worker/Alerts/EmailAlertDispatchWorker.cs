using Microsoft.Extensions.Options;
using THub.Application.Alerts;
using THub.Domain.Alerts;

namespace THub.Worker.Alerts;

public sealed class EmailAlertDispatchWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<EmailAlertDispatchWorkerOptions> options,
    ILogger<EmailAlertDispatchWorker> logger) : BackgroundService
{
    private readonly EmailAlertDispatchWorkerOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly string _leaseOwner = CreateLeaseOwner();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dispatchOptions = _options.CreateDispatchOptions();
        logger.LogInformation(
            "Email alert dispatcher {LeaseOwner} started with batch size {BatchSize}.",
            _leaseOwner,
            dispatchOptions.MaximumDeliveriesPerBatch);

        while (!stoppingToken.IsCancellationRequested)
        {
            AlertDispatchBatchResult? result = null;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<AlertDeliveryDispatcher>();
                result = await dispatcher.DispatchBatchAsync(
                    _leaseOwner,
                    dispatchOptions,
                    stoppingToken);
                if (result.Claimed > 0)
                {
                    logger.LogInformation(
                        "Email dispatch batch claimed {Claimed}, delivered {Delivered}, scheduled {RetryScheduled} retries, dead-lettered {DeadLettered}, and saw {Conflicts} transition conflicts.",
                        result.Claimed,
                        result.Delivered,
                        result.RetryScheduled,
                        result.DeadLettered,
                        result.TransitionConflicts);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Email dispatch batch failed; leased work remains recoverable.");
            }

            if (result?.Claimed == dispatchOptions.MaximumDeliveriesPerBatch)
            {
                continue;
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static string CreateLeaseOwner()
    {
        var value = $"email:{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        return value.Length <= AlertDelivery.MaximumLeaseOwnerLength
            ? value
            : value[..AlertDelivery.MaximumLeaseOwnerLength];
    }
}
