using Microsoft.Extensions.Options;
using THub.Application.Publications;
using THub.Domain.Publications;

namespace THub.Worker.Publications;

public sealed class PublicationChangeSetWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<PublicationChangeSetWorkerOptions> options,
    ILogger<PublicationChangeSetWorker> logger) : BackgroundService
{
    private readonly PublicationChangeSetWorkerOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly string _workerId = CreateWorkerId();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Publication change-set applier {WorkerId} started.", _workerId);
        while (!stoppingToken.IsCancellationRequested)
        {
            PublicationChangeSetProcessResult? result = null;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IPublicationChangeSetProcessor>();
                result = await processor.ProcessNextAsync(_workerId, stoppingToken);
                if (result.Status != PublicationChangeSetProcessStatus.NoWork)
                {
                    logger.LogInformation(
                        "Publication change set {ChangeSetId} completed processor pass with status {Status}.",
                        result.ChangeSetId,
                        result.Status);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Publication change-set processing failed; leases remain recoverable.");
            }

            if (result is not null &&
                result.Status is PublicationChangeSetProcessStatus.Applied
                    or PublicationChangeSetProcessStatus.Conflict
                    or PublicationChangeSetProcessStatus.Failed)
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

    private static string CreateWorkerId()
    {
        var value = $"publication:{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        return value.Length <= Publication.MaximumIdentityLength
            ? value
            : value[..Publication.MaximumIdentityLength];
    }
}
