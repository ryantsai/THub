using Microsoft.Extensions.Options;
using THub.Application.Alerts;
using THub.Application.Execution;
using THub.Domain.Runs;
using THub.Domain.Workflows;

namespace THub.Worker.Execution;

public sealed class WorkflowExecutionWorker(
    IWorkflowRunExecutionStore executionStore,
    IServiceScopeFactory scopeFactory,
    IOptions<WorkflowExecutionWorkerOptions> options,
    TimeProvider timeProvider,
    ILogger<WorkflowExecutionWorker> logger) : BackgroundService
{
    private readonly WorkflowExecutionWorkerOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly string _leaseOwner = CreateLeaseOwner();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var running = new HashSet<Task>();
        logger.LogInformation(
            "Workflow execution dispatcher {LeaseOwner} started with maximum concurrency {MaximumConcurrency}.",
            _leaseOwner,
            _options.MaximumConcurrency);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                RemoveCompleted(running);
                var claimedAny = false;
                while (running.Count < _options.MaximumConcurrency
                    && !stoppingToken.IsCancellationRequested)
                {
                    WorkflowRunExecutionClaim? claim;
                    try
                    {
                        claim = await executionStore.TryClaimNextAsync(
                            _leaseOwner,
                            timeProvider.GetUtcNow(),
                            _options.LeaseDuration,
                            stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(exception, "Workflow run claim failed.");
                        break;
                    }

                    if (claim is null)
                    {
                        break;
                    }

                    claimedAny = true;
                    var task = ExecuteClaimProtectedAsync(claim, stoppingToken);
                    running.Add(task);
                }

                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                if (claimedAny && running.Count < _options.MaximumConcurrency)
                {
                    continue;
                }

                var poll = Task.Delay(_options.PollInterval, stoppingToken);
                if (running.Count == 0)
                {
                    await poll;
                }
                else
                {
                    _ = await Task.WhenAny(running.Append(poll));
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        finally
        {
            if (running.Count > 0)
            {
                await Task.WhenAll(running);
            }
        }
    }

    private async Task ExecuteClaimProtectedAsync(
        WorkflowRunExecutionClaim claim,
        CancellationToken stoppingToken)
    {
        try
        {
            await ExecuteClaimAsync(claim, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation(
                "Workflow run {WorkflowRunId} stopped with the host and remains recoverable by lease expiry.",
                claim.WorkflowRunId);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Workflow run {WorkflowRunId} left its lease recoverable after an unexpected dispatcher failure.",
                claim.WorkflowRunId);
        }
    }

    private async Task ExecuteClaimAsync(
        WorkflowRunExecutionClaim claim,
        CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var engineFactory = scope.ServiceProvider.GetRequiredService<WorkflowExecutionEngineFactory>();
        var terminalAlerts = scope.ServiceProvider.GetRequiredService<WorkflowTerminalAlertService>();
        var signals = new ExecutionSignals(claim.CancellationRequested);
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var heartbeatStop = new CancellationTokenSource();
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            heartbeatStop.Token);
        if (claim.CancellationRequested)
        {
            executionCancellation.Cancel();
        }

        var heartbeat = RunHeartbeatAsync(
            claim.WorkflowRunId,
            signals,
            executionCancellation,
            heartbeatCancellation.Token);
        WorkflowExecutionResult result;
        try
        {
            if (!IsExactSnapshot(claim))
            {
                result = FailedSnapshot(claim.WorkflowRunId);
            }
            else
            {
                var engine = engineFactory.Create(claim.WorkflowRunId, _leaseOwner, _options);
                result = await engine.ExecuteSerializedAsync(
                    claim.WorkflowRunId,
                    claim.GraphJson,
                    executionCancellation.Token);
            }

            if (signals.LeaseUncertain || (stoppingToken.IsCancellationRequested && !signals.DurableCancellation))
            {
                return;
            }

            await CommitTerminalAsync(
                claim.WorkflowRunId,
                result,
                signals,
                terminalAlerts,
                stoppingToken);
        }
        finally
        {
            heartbeatStop.Cancel();
            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException) when (heartbeatCancellation.IsCancellationRequested)
            {
                // Expected after completion or host shutdown.
            }
        }
    }

    private async Task RunHeartbeatAsync(
        Guid workflowRunId,
        ExecutionSignals signals,
        CancellationTokenSource executionCancellation,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            WorkflowLeaseRenewalStatus status;
            try
            {
                status = await executionStore.RenewLeaseAsync(
                    workflowRunId,
                    _leaseOwner,
                    timeProvider.GetUtcNow(),
                    _options.LeaseDuration,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Heartbeat failed for workflow run {WorkflowRunId}.", workflowRunId);
                signals.MarkLeaseUncertain();
                executionCancellation.Cancel();
                return;
            }

            switch (status)
            {
                case WorkflowLeaseRenewalStatus.Renewed:
                    break;
                case WorkflowLeaseRenewalStatus.CancellationRequested:
                    signals.MarkDurableCancellation();
                    executionCancellation.Cancel();
                    break;
                case WorkflowLeaseRenewalStatus.LeaseLost:
                case WorkflowLeaseRenewalStatus.NotFound:
                    signals.MarkLeaseUncertain();
                    executionCancellation.Cancel();
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(status));
            }
        }
    }

    private async Task CommitTerminalAsync(
        Guid workflowRunId,
        WorkflowExecutionResult result,
        ExecutionSignals signals,
        WorkflowTerminalAlertService terminalAlerts,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var now = timeProvider.GetUtcNow();
            var run = await executionStore.LoadOwnedRunAsync(
                workflowRunId,
                _leaseOwner,
                now,
                cancellationToken);
            if (run is null)
            {
                signals.MarkLeaseUncertain();
                return;
            }

            ApplyTerminalTransition(run, result, now);
            var commit = await terminalAlerts.CommitAsync(
                new CommitTerminalRunWithAlertsCommand(
                    run,
                    WorkflowRunStatus.Running,
                    _leaseOwner),
                cancellationToken);
            if (commit.IsSuccess)
            {
                logger.LogInformation(
                    "Workflow run {WorkflowRunId} completed with status {Status}.",
                    workflowRunId,
                    run.Status);
                return;
            }

            if (commit.Problem?.Code is not ("email.alert_policy_changed" or "email.terminal_commit_conflict")
                || attempt == 3)
            {
                logger.LogWarning(
                    "Terminal commit for workflow run {WorkflowRunId} was not saved: {ProblemCode}.",
                    workflowRunId,
                    commit.Problem?.Code);
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken);
        }

    }

    private static void ApplyTerminalTransition(
        WorkflowRun run,
        WorkflowExecutionResult result,
        DateTimeOffset completedAtUtc)
    {
        if (run.CancellationRequested)
        {
            run.CompleteCancelled(run.LeaseOwner!, completedAtUtc);
            return;
        }

        switch (result.Status)
        {
            case WorkflowExecutionStatus.Succeeded:
                run.CompleteSucceeded(run.LeaseOwner!, completedAtUtc);
                break;
            case WorkflowExecutionStatus.Failed:
                run.CompleteFailed(
                    run.LeaseOwner!,
                    result.Error ?? new ExecutionError(
                        "execution.failed",
                        ExecutionErrorCategory.Unknown,
                        "Workflow execution failed without a normalized error.",
                        isRetryable: false),
                    completedAtUtc);
                break;
            case WorkflowExecutionStatus.Cancelled:
                run.CompleteFailed(
                    run.LeaseOwner!,
                    new ExecutionError(
                        "execution.cancelled_without_request",
                        ExecutionErrorCategory.Unknown,
                        "Execution stopped without an authoritative cancellation request.",
                        isRetryable: false),
                    completedAtUtc);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(result));
        }
    }

    private static bool IsExactSnapshot(WorkflowRunExecutionClaim claim) =>
        claim.WorkflowVersionId == WorkflowVersion.CreateId(claim.WorkflowId, claim.WorkflowVersion)
        && string.Equals(
            WorkflowVersion.ComputeChecksum(claim.GraphJson),
            claim.Checksum,
            StringComparison.OrdinalIgnoreCase);

    private static WorkflowExecutionResult FailedSnapshot(Guid workflowRunId) => new(
        workflowRunId,
        WorkflowExecutionStatus.Failed,
        [],
        new ExecutionError(
            "workflow.version.integrity",
            ExecutionErrorCategory.Validation,
            "The immutable workflow version failed its identity or checksum validation.",
            isRetryable: false));

    private static string CreateLeaseOwner()
    {
        var machine = Environment.MachineName;
        var value = $"{machine}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        return value.Length <= WorkflowRun.MaximumLeaseOwnerLength
            ? value
            : value[..WorkflowRun.MaximumLeaseOwnerLength];
    }

    private static void RemoveCompleted(ISet<Task> running)
    {
        foreach (var completed in running.Where(task => task.IsCompleted).ToArray())
        {
            running.Remove(completed);
        }
    }

    private sealed class ExecutionSignals(bool durableCancellation)
    {
        private int _durableCancellation = durableCancellation ? 1 : 0;
        private int _leaseUncertain;

        public bool DurableCancellation => Volatile.Read(ref _durableCancellation) != 0;

        public bool LeaseUncertain => Volatile.Read(ref _leaseUncertain) != 0;

        public void MarkDurableCancellation() => Interlocked.Exchange(ref _durableCancellation, 1);

        public void MarkLeaseUncertain() => Interlocked.Exchange(ref _leaseUncertain, 1);
    }
}
