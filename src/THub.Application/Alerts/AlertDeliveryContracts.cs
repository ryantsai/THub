using THub.Domain.Alerts;
using THub.Domain.Runs;

namespace THub.Application.Alerts;

public sealed record ClaimedAlertDelivery(
    AlertDelivery Delivery,
    EmailDeliveryProfile Profile);

public enum AlertDeliveryTransitionStatus
{
    Saved,
    NotFound,
    LeaseLost,
    Conflict
}

public enum AlertEnqueueStatus
{
    Enqueued,
    AlreadyEnqueued,
    ReferencedResourceUnavailable,
    Conflict
}

public sealed record AlertEnqueueStoreResult(
    AlertEnqueueStatus Status,
    Guid? DeliveryId = null);

public interface IAlertDeliveryStore
{
    Task<AlertEnqueueStoreResult> EnqueueEmailActionAsync(
        AlertDelivery delivery,
        CancellationToken cancellationToken);

    Task<ClaimedAlertDelivery?> TryClaimNextAsync(
        string leaseOwner,
        DateTimeOffset claimedAtUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<AlertDeliveryTransitionStatus> RecordDeliveredAsync(
        Guid deliveryId,
        string leaseOwner,
        DateTimeOffset deliveredAtUtc,
        string? providerMessageId,
        CancellationToken cancellationToken);

    Task<AlertDeliveryTransitionStatus> RecordFailureAsync(
        Guid deliveryId,
        string leaseOwner,
        ExecutionError error,
        DateTimeOffset failedAtUtc,
        DateTimeOffset? nextAttemptAtUtc,
        CancellationToken cancellationToken);
}

public sealed class SmtpCredential
{
    public const int MaximumUserNameLength = 320;
    public const int MaximumPasswordLength = 4_096;

    public SmtpCredential(string userName, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentNullException.ThrowIfNull(password);
        if (userName.Length > MaximumUserNameLength)
        {
            throw new ArgumentOutOfRangeException(nameof(userName));
        }

        if (password.Length is < 1 or > MaximumPasswordLength)
        {
            throw new ArgumentOutOfRangeException(nameof(password));
        }

        UserName = userName;
        Password = password;
    }

    public string UserName { get; }

    /// <summary>
    /// Secret material for the SMTP adapter. Consumers must never persist, return, or log it.
    /// </summary>
    public string Password { get; }
}

public interface ISecretResolver
{
    ValueTask<SmtpCredential?> ResolveSmtpCredentialAsync(
        string secretReference,
        CancellationToken cancellationToken);
}

public interface IAlertSender
{
    ValueTask<AlertSendResult> SendAsync(
        EmailDeliveryProfile profile,
        AlertDelivery delivery,
        CancellationToken cancellationToken);
}

public sealed record AlertSendResult
{
    private AlertSendResult(bool succeeded, string? providerMessageId, ExecutionError? error)
    {
        if (succeeded == (error is not null))
        {
            throw new ArgumentException(
                "A send result must contain either success or one normalized error.");
        }

        Succeeded = succeeded;
        ProviderMessageId = providerMessageId;
        Error = error;
    }

    public bool Succeeded { get; }

    public string? ProviderMessageId { get; }

    public ExecutionError? Error { get; }

    public static AlertSendResult Success(string? providerMessageId = null) =>
        new(succeeded: true, providerMessageId, error: null);

    public static AlertSendResult Failure(ExecutionError error) =>
        new(succeeded: false, providerMessageId: null, error);
}

public sealed record AlertRetryPolicy
{
    public static AlertRetryPolicy Default { get; } = new(
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(30),
        jitterRatio: 0.2);

    public AlertRetryPolicy(
        TimeSpan initialDelay,
        TimeSpan maximumDelay,
        double jitterRatio)
    {
        if (initialDelay < TimeSpan.FromSeconds(1)
            || initialDelay > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(initialDelay));
        }

        if (maximumDelay < initialDelay || maximumDelay > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDelay));
        }

        if (jitterRatio is < 0 or > 0.5)
        {
            throw new ArgumentOutOfRangeException(nameof(jitterRatio));
        }

        InitialDelay = initialDelay;
        MaximumDelay = maximumDelay;
        JitterRatio = jitterRatio;
    }

    public TimeSpan InitialDelay { get; }

    public TimeSpan MaximumDelay { get; }

    public double JitterRatio { get; }

    public TimeSpan GetDelay(Guid deliveryId, int attemptCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attemptCount);

        var exponent = Math.Min(attemptCount - 1, 30);
        var multiplier = Math.Pow(2, exponent);
        var uncappedTicks = InitialDelay.Ticks * multiplier;
        var cappedTicks = Math.Min(uncappedTicks, MaximumDelay.Ticks);
        var seed = deliveryId.ToByteArray();
        var sample = (BitConverter.ToUInt32(seed, 0) ^ (uint)attemptCount) / (double)uint.MaxValue;
        var jitterFactor = 1 - JitterRatio + (2 * JitterRatio * sample);
        var jitteredTicks = Math.Clamp(
            (long)(cappedTicks * jitterFactor),
            TimeSpan.FromSeconds(1).Ticks,
            MaximumDelay.Ticks);
        return TimeSpan.FromTicks(jitteredTicks);
    }
}

public sealed record AlertDispatchOptions
{
    public AlertDispatchOptions(
        int maximumDeliveriesPerBatch = 25,
        TimeSpan? leaseDuration = null,
        AlertRetryPolicy? retryPolicy = null,
        TimeSpan? transitionTimeout = null)
    {
        if (maximumDeliveriesPerBatch is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDeliveriesPerBatch));
        }

        var normalizedLease = leaseDuration ?? TimeSpan.FromMinutes(2);
        if (normalizedLease < TimeSpan.FromSeconds(30)
            || normalizedLease > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        var normalizedTransitionTimeout = transitionTimeout ?? TimeSpan.FromSeconds(15);
        if (normalizedTransitionTimeout < TimeSpan.FromSeconds(5)
            || normalizedTransitionTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(transitionTimeout));
        }

        MaximumDeliveriesPerBatch = maximumDeliveriesPerBatch;
        LeaseDuration = normalizedLease;
        RetryPolicy = retryPolicy ?? AlertRetryPolicy.Default;
        TransitionTimeout = normalizedTransitionTimeout;
    }

    public int MaximumDeliveriesPerBatch { get; }

    public TimeSpan LeaseDuration { get; }

    public AlertRetryPolicy RetryPolicy { get; }

    public TimeSpan TransitionTimeout { get; }
}

public sealed record AlertDispatchBatchResult(
    int Claimed,
    int Delivered,
    int RetryScheduled,
    int DeadLettered,
    int TransitionConflicts)
{
    public static AlertDispatchBatchResult Empty { get; } = new(0, 0, 0, 0, 0);
}
