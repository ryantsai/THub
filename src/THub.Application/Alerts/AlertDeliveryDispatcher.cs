using THub.Domain.Runs;

namespace THub.Application.Alerts;

public sealed class AlertDeliveryDispatcher(
    IAlertDeliveryStore deliveryStore,
    IAlertSender alertSender,
    TimeProvider timeProvider)
{
    private readonly IAlertDeliveryStore _deliveryStore =
        deliveryStore ?? throw new ArgumentNullException(nameof(deliveryStore));
    private readonly IAlertSender _alertSender =
        alertSender ?? throw new ArgumentNullException(nameof(alertSender));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<AlertDispatchBatchResult> DispatchBatchAsync(
        string leaseOwner,
        AlertDispatchOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        ArgumentNullException.ThrowIfNull(options);

        var claimedCount = 0;
        var deliveredCount = 0;
        var retryCount = 0;
        var deadLetterCount = 0;
        var conflictCount = 0;

        for (var index = 0; index < options.MaximumDeliveriesPerBatch; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var claimed = await _deliveryStore.TryClaimNextAsync(
                    leaseOwner,
                    _timeProvider.GetUtcNow(),
                    options.LeaseDuration,
                    cancellationToken)
                .ConfigureAwait(false);
            if (claimed is null)
            {
                break;
            }

            claimedCount++;
            AlertSendResult sendResult;
            try
            {
                sendResult = await _alertSender.SendAsync(
                        claimed.Profile,
                        claimed.Delivery,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Leave the delivery leased. Another dispatcher recovers it after expiry.
                throw;
            }
            catch (Exception)
            {
                sendResult = AlertSendResult.Failure(new ExecutionError(
                    "email.sender_unexpected",
                    ExecutionErrorCategory.Unknown,
                    "The Email sender failed unexpectedly.",
                    isRetryable: true));
            }

            var transitionAt = _timeProvider.GetUtcNow();
            // Once SMTP may have accepted a message, host shutdown cancellation must not skip the
            // durable outcome write. Use a separate short bound; timeout still leaves the lease
            // recoverable under the documented at-least-once ambiguity.
            using var transitionCancellation = new CancellationTokenSource(
                options.TransitionTimeout);
            if (sendResult.Succeeded)
            {
                var status = await _deliveryStore.RecordDeliveredAsync(
                        claimed.Delivery.Id,
                        leaseOwner,
                        transitionAt,
                        sendResult.ProviderMessageId,
                        transitionCancellation.Token)
                    .ConfigureAwait(false);
                if (status == AlertDeliveryTransitionStatus.Saved)
                {
                    deliveredCount++;
                }
                else
                {
                    conflictCount++;
                }

                continue;
            }

            var error = sendResult.Error!;
            var canRetry = error.IsRetryable
                && claimed.Delivery.AttemptCount < claimed.Delivery.MaximumAttempts;
            var nextAttemptAt = canRetry
                ? transitionAt.Add(options.RetryPolicy.GetDelay(
                    claimed.Delivery.Id,
                    claimed.Delivery.AttemptCount))
                : (DateTimeOffset?)null;
            var failureStatus = await _deliveryStore.RecordFailureAsync(
                    claimed.Delivery.Id,
                    leaseOwner,
                    error,
                    transitionAt,
                    nextAttemptAt,
                    transitionCancellation.Token)
                .ConfigureAwait(false);
            if (failureStatus != AlertDeliveryTransitionStatus.Saved)
            {
                conflictCount++;
            }
            else if (canRetry)
            {
                retryCount++;
            }
            else
            {
                deadLetterCount++;
            }
        }

        return new AlertDispatchBatchResult(
            claimedCount,
            deliveredCount,
            retryCount,
            deadLetterCount,
            conflictCount);
    }
}
