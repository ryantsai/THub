using System.Net.Http;
using THub.Domain.Runs;
using THub.Domain.Workflows;

namespace THub.Application.Execution;

public sealed record NodeRetryPolicy
{
    public const int AbsoluteMaximumAttempts = 10;
    private static readonly TimeSpan AbsoluteMaximumDelay = TimeSpan.FromHours(1);

    public NodeRetryPolicy(
        int maximumAttempts,
        TimeSpan initialDelay,
        TimeSpan maximumDelay,
        double jitterRatio = 0.2)
    {
        if (maximumAttempts is < 1 or > AbsoluteMaximumAttempts)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumAttempts),
                $"Maximum attempts must be 1 to {AbsoluteMaximumAttempts}.");
        }

        if (initialDelay < TimeSpan.Zero || initialDelay > AbsoluteMaximumDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(initialDelay));
        }

        if (maximumDelay < initialDelay || maximumDelay > AbsoluteMaximumDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDelay));
        }

        if (!double.IsFinite(jitterRatio) || jitterRatio is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(jitterRatio));
        }

        MaximumAttempts = maximumAttempts;
        InitialDelay = initialDelay;
        MaximumDelay = maximumDelay;
        JitterRatio = jitterRatio;
    }

    public int MaximumAttempts { get; }

    public TimeSpan InitialDelay { get; }

    public TimeSpan MaximumDelay { get; }

    public double JitterRatio { get; }

    public static NodeRetryPolicy NoRetry { get; } = new(1, TimeSpan.Zero, TimeSpan.Zero, 0);

    public static NodeRetryPolicy ConservativeTransient { get; } = new(
        3,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(30));
}

public interface INodeRetryPolicyProvider
{
    NodeRetryPolicy GetPolicy(
        WorkflowNode node,
        WorkflowNodeExecutorDescriptor executorDescriptor);
}

public sealed class DefaultNodeRetryPolicyProvider : INodeRetryPolicyProvider
{
    private readonly NodeRetryPolicy _retryablePolicy;

    public DefaultNodeRetryPolicyProvider(NodeRetryPolicy? retryablePolicy = null)
    {
        _retryablePolicy = retryablePolicy ?? NodeRetryPolicy.ConservativeTransient;
    }

    public NodeRetryPolicy GetPolicy(
        WorkflowNode node,
        WorkflowNodeExecutorDescriptor executorDescriptor)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(executorDescriptor);
        return executorDescriptor.RetrySafety == WorkflowNodeRetrySafety.Never
            ? NodeRetryPolicy.NoRetry
            : _retryablePolicy;
    }
}

public sealed record WorkflowExecutionTimeoutOptions
{
    private static readonly TimeSpan AbsoluteMaximumTimeout = TimeSpan.FromHours(24);

    public WorkflowExecutionTimeoutOptions(
        TimeSpan? maximumRunDuration = null,
        TimeSpan? defaultNodeAttemptTimeout = null)
    {
        MaximumRunDuration = Validate(
            maximumRunDuration ?? TimeSpan.FromHours(12),
            nameof(maximumRunDuration));
        DefaultNodeAttemptTimeout = Validate(
            defaultNodeAttemptTimeout ?? TimeSpan.FromMinutes(30),
            nameof(defaultNodeAttemptTimeout));
    }

    public TimeSpan MaximumRunDuration { get; }

    public TimeSpan DefaultNodeAttemptTimeout { get; }

    public static TimeSpan Validate(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value > AbsoluteMaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Timeout must be positive and cannot exceed {AbsoluteMaximumTimeout}.");
        }

        return value;
    }
}

public interface INodeExecutionTimeoutProvider
{
    TimeSpan GetAttemptTimeout(
        WorkflowNode node,
        WorkflowNodeExecutorDescriptor executorDescriptor);
}

public sealed class DefaultNodeExecutionTimeoutProvider : INodeExecutionTimeoutProvider
{
    private readonly TimeSpan _defaultTimeout;

    public DefaultNodeExecutionTimeoutProvider(WorkflowExecutionTimeoutOptions? options = null)
    {
        _defaultTimeout = (options ?? new WorkflowExecutionTimeoutOptions()).DefaultNodeAttemptTimeout;
    }

    public TimeSpan GetAttemptTimeout(
        WorkflowNode node,
        WorkflowNodeExecutorDescriptor executorDescriptor)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(executorDescriptor);
        return _defaultTimeout;
    }
}

public interface IWorkflowRetryScheduler
{
    TimeSpan GetDelay(NodeRetryPolicy policy, int failedAttempt);

    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class ExponentialJitterRetryScheduler : IWorkflowRetryScheduler
{
    public TimeSpan GetDelay(NodeRetryPolicy policy, int failedAttempt)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(failedAttempt);

        var exponent = Math.Min(failedAttempt - 1, 30);
        var uncappedTicks = policy.InitialDelay.Ticks * Math.Pow(2, exponent);
        var cappedTicks = Math.Min(uncappedTicks, policy.MaximumDelay.Ticks);
        var jitterMultiplier = policy.JitterRatio == 0
            ? 1
            : 1 + ((Random.Shared.NextDouble() * 2) - 1) * policy.JitterRatio;
        var jitteredTicks = Math.Clamp(
            cappedTicks * jitterMultiplier,
            0,
            policy.MaximumDelay.Ticks);
        return TimeSpan.FromTicks((long)jitteredTicks);
    }

    public async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}

public interface IExecutionErrorClassifier
{
    ExecutionError Classify(Exception exception, bool cancellationRequested);
}

public sealed class DefaultExecutionErrorClassifier : IExecutionErrorClassifier
{
    public ExecutionError Classify(Exception exception, bool cancellationRequested)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is WorkflowNodeExecutionException nodeException)
        {
            return nodeException.Error;
        }

        if (exception is OperationCanceledException && cancellationRequested)
        {
            return new ExecutionError(
                "execution.cancelled",
                ExecutionErrorCategory.Cancelled,
                "Execution was cancelled.",
                isRetryable: false);
        }

        return exception switch
        {
            WorkflowExecutionEventSinkException => new ExecutionError(
                "execution.event_sink.unavailable",
                ExecutionErrorCategory.Connectivity,
                "The durable execution event sink is unavailable.",
                isRetryable: true),
            TabularLimitExceededException limit => new ExecutionError(
                limit.Code,
                ExecutionErrorCategory.ResourceLimit,
                limit.Message,
                isRetryable: false),
            TabularContractException contract => new ExecutionError(
                contract.Code,
                ExecutionErrorCategory.Data,
                contract.Message,
                isRetryable: false),
            TimeoutException => new ExecutionError(
                "execution.timeout",
                ExecutionErrorCategory.Timeout,
                "The operation exceeded its allowed time.",
                isRetryable: true),
            HttpRequestException http => ClassifyHttp(http),
            FileNotFoundException => new ExecutionError(
                "execution.file.not_found",
                ExecutionErrorCategory.Configuration,
                "The configured file does not exist.",
                isRetryable: false),
            DirectoryNotFoundException => new ExecutionError(
                "execution.directory.not_found",
                ExecutionErrorCategory.Configuration,
                "The configured directory does not exist.",
                isRetryable: false),
            DriveNotFoundException => new ExecutionError(
                "execution.drive.not_found",
                ExecutionErrorCategory.Configuration,
                "The configured drive is unavailable or does not exist.",
                isRetryable: false),
            PathTooLongException => new ExecutionError(
                "execution.path.too_long",
                ExecutionErrorCategory.Configuration,
                "The configured path exceeds the supported length.",
                isRetryable: false),
            IOException io when IsStorageCapacityError(io) => new ExecutionError(
                "execution.storage.capacity",
                ExecutionErrorCategory.ResourceLimit,
                "The storage resource has insufficient capacity.",
                isRetryable: false),
            IOException => new ExecutionError(
                "execution.connectivity.io",
                ExecutionErrorCategory.Connectivity,
                "The data resource could not be read or written.",
                isRetryable: true),
            UnauthorizedAccessException => new ExecutionError(
                "execution.authorization",
                ExecutionErrorCategory.Authorization,
                "The worker is not authorized to access the configured resource.",
                isRetryable: false),
            OperationCanceledException => new ExecutionError(
                "execution.operation_cancelled",
                ExecutionErrorCategory.Cancelled,
                "The node operation was cancelled.",
                isRetryable: false),
            _ => new ExecutionError(
                "execution.unexpected",
                ExecutionErrorCategory.Unknown,
                "The node failed unexpectedly. Inspect protected worker logs using the run correlation id.",
                isRetryable: false)
        };
    }

    private static ExecutionError ClassifyHttp(HttpRequestException exception)
    {
        var statusCode = exception.StatusCode;
        var rateLimited = statusCode == System.Net.HttpStatusCode.TooManyRequests;
        var retryable = statusCode is null
            || statusCode == System.Net.HttpStatusCode.RequestTimeout
            || rateLimited
            || (int)statusCode >= 500;
        return new ExecutionError(
            rateLimited ? "execution.rate_limited.http" : "execution.connectivity.http",
            rateLimited ? ExecutionErrorCategory.RateLimited : ExecutionErrorCategory.Connectivity,
            retryable
                ? "The remote service could not complete the request."
                : "The remote service rejected the request.",
            retryable);
    }

    private static bool IsStorageCapacityError(IOException exception) =>
        exception.HResult is unchecked((int)0x80070027) or unchecked((int)0x80070070);
}
