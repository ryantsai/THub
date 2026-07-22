using THub.Domain.Workflows;

namespace THub.Domain.Runs;

public enum WorkflowRunStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public sealed class WorkflowRun
{
    public const int MaximumTriggeredByLength = 256;
    public const int MaximumLeaseOwnerLength = 200;
    public const int MaximumAttempts = 1_000;

    private static readonly TimeSpan MaximumLeaseDuration = TimeSpan.FromHours(24);

    private WorkflowRun() { }

    /// <summary>
    /// Compatibility constructor for the existing scheduler. Immutable workflow-version
    /// identifiers are deterministic from workflow identity and version number.
    /// </summary>
    public WorkflowRun(
        Guid workflowId,
        int workflowVersion,
        string triggeredBy,
        DateTimeOffset? scheduledForUtc = null)
        : this(
            workflowId,
            THub.Domain.Workflows.WorkflowVersion.CreateId(workflowId, workflowVersion),
            workflowVersion,
            triggeredBy,
            DateTimeOffset.UtcNow,
            scheduledForUtc)
    {
    }

    public WorkflowRun(
        Guid workflowId,
        Guid workflowVersionId,
        int workflowVersion,
        string triggeredBy,
        DateTimeOffset queuedAtUtc,
        DateTimeOffset? scheduledForUtc = null,
        Guid? retryOfRunId = null)
    {
        Id = Guid.NewGuid();
        WorkflowId = DomainGuard.RequireId(workflowId, nameof(workflowId));
        WorkflowVersionId = DomainGuard.RequireId(
            workflowVersionId,
            nameof(workflowVersionId));
        WorkflowVersion = DomainGuard.RequirePositive(
            workflowVersion,
            nameof(workflowVersion));
        TriggeredBy = DomainGuard.Require(
            triggeredBy,
            nameof(triggeredBy),
            MaximumTriggeredByLength);
        QueuedAtUtc = DomainGuard.Utc(queuedAtUtc, nameof(queuedAtUtc));
        ScheduledForUtc = scheduledForUtc?.ToUniversalTime();

        if (retryOfRunId == Guid.Empty)
        {
            throw new ArgumentException(
                "A retry identity must be non-empty when supplied.",
                nameof(retryOfRunId));
        }

        if (retryOfRunId == Id)
        {
            throw new ArgumentException("A run cannot retry itself.", nameof(retryOfRunId));
        }

        RetryOfRunId = retryOfRunId;
    }

    public Guid Id { get; private set; }

    public Guid WorkflowId { get; private set; }

    public Guid WorkflowVersionId { get; private set; }

    public int WorkflowVersion { get; private set; }

    public Guid? RetryOfRunId { get; private set; }

    public WorkflowRunStatus Status { get; private set; } = WorkflowRunStatus.Queued;

    public string TriggeredBy { get; private set; } = string.Empty;

    public DateTimeOffset? ScheduledForUtc { get; private set; }

    public DateTimeOffset QueuedAtUtc { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public int AttemptCount { get; private set; }

    public string? LeaseOwner { get; private set; }

    public DateTimeOffset? LeaseExpiresAtUtc { get; private set; }

    public DateTimeOffset? LastHeartbeatAtUtc { get; private set; }

    public DateTimeOffset? CancellationRequestedAtUtc { get; private set; }

    public string? CancellationRequestedBy { get; private set; }

    public ExecutionError? Error { get; private set; }

    /// <summary>
    /// Compatibility projection for existing history screens. New code should use <see cref="Error"/>.
    /// </summary>
    public string? ErrorMessage { get; private set; }

    public bool IsTerminal =>
        Status is WorkflowRunStatus.Succeeded
            or WorkflowRunStatus.Failed
            or WorkflowRunStatus.Cancelled;

    public bool CancellationRequested => CancellationRequestedAtUtc is not null;

    public bool TryClaim(
        string leaseOwner,
        DateTimeOffset claimedAtUtc,
        TimeSpan leaseDuration)
    {
        var owner = DomainGuard.Require(
            leaseOwner,
            nameof(leaseOwner),
            MaximumLeaseOwnerLength);
        var timestamp = DomainGuard.OnOrAfter(
            claimedAtUtc,
            QueuedAtUtc,
            nameof(claimedAtUtc));
        var duration = DomainGuard.LeaseDuration(
            leaseDuration,
            nameof(leaseDuration),
            MaximumLeaseDuration);

        if (IsTerminal)
        {
            return false;
        }

        if (Status == WorkflowRunStatus.Running && !IsLeaseExpired(timestamp))
        {
            return false;
        }

        if (AttemptCount >= MaximumAttempts)
        {
            throw new InvalidOperationException(
                $"A run cannot exceed {MaximumAttempts} execution attempts.");
        }

        Status = WorkflowRunStatus.Running;
        StartedAtUtc ??= timestamp;
        AttemptCount++;
        LeaseOwner = owner;
        LastHeartbeatAtUtc = timestamp;
        LeaseExpiresAtUtc = timestamp.Add(duration);
        return true;
    }

    public void RenewLease(
        string leaseOwner,
        DateTimeOffset heartbeatAtUtc,
        TimeSpan leaseDuration)
    {
        var timestamp = DomainGuard.OnOrAfter(
            heartbeatAtUtc,
            LastHeartbeatAtUtc ?? QueuedAtUtc,
            nameof(heartbeatAtUtc));
        EnsureActiveLease(leaseOwner, timestamp);
        var duration = DomainGuard.LeaseDuration(
            leaseDuration,
            nameof(leaseDuration),
            MaximumLeaseDuration);

        LastHeartbeatAtUtc = timestamp;
        LeaseExpiresAtUtc = timestamp.Add(duration);
    }

    public bool RequestCancellation(string requestedBy, DateTimeOffset requestedAtUtc)
    {
        var actor = DomainGuard.Require(
            requestedBy,
            nameof(requestedBy),
            MaximumTriggeredByLength);
        var timestamp = DomainGuard.OnOrAfter(
            requestedAtUtc,
            QueuedAtUtc,
            nameof(requestedAtUtc));

        if (IsTerminal || CancellationRequested)
        {
            return false;
        }

        CancellationRequestedBy = actor;
        CancellationRequestedAtUtc = timestamp;

        if (Status == WorkflowRunStatus.Queued)
        {
            TransitionToTerminal(WorkflowRunStatus.Cancelled, timestamp, error: null);
        }

        return true;
    }

    public void CompleteSucceeded(string leaseOwner, DateTimeOffset completedAtUtc)
    {
        var timestamp = ValidateTerminalTransition(leaseOwner, completedAtUtc);
        if (CancellationRequested)
        {
            throw new InvalidOperationException(
                "A run with a durable cancellation request cannot be completed as succeeded.");
        }

        TransitionToTerminal(WorkflowRunStatus.Succeeded, timestamp, error: null);
    }

    public void CompleteFailed(
        string leaseOwner,
        ExecutionError error,
        DateTimeOffset completedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(error);
        var timestamp = ValidateTerminalTransition(leaseOwner, completedAtUtc);
        TransitionToTerminal(WorkflowRunStatus.Failed, timestamp, error);
    }

    public void CompleteCancelled(string leaseOwner, DateTimeOffset completedAtUtc)
    {
        if (!CancellationRequested)
        {
            throw new InvalidOperationException(
                "A running workflow can be cancelled only after a durable cancellation request.");
        }

        var timestamp = ValidateTerminalTransition(leaseOwner, completedAtUtc);
        TransitionToTerminal(WorkflowRunStatus.Cancelled, timestamp, error: null);
    }

    public bool IsLeaseExpired(DateTimeOffset atUtc)
    {
        var timestamp = DomainGuard.Utc(atUtc, nameof(atUtc));
        return LeaseExpiresAtUtc is null || LeaseExpiresAtUtc <= timestamp;
    }

    private DateTimeOffset ValidateTerminalTransition(
        string leaseOwner,
        DateTimeOffset completedAtUtc)
    {
        var timestamp = DomainGuard.OnOrAfter(
            completedAtUtc,
            LastHeartbeatAtUtc ?? StartedAtUtc ?? QueuedAtUtc,
            nameof(completedAtUtc));
        EnsureActiveLease(leaseOwner, timestamp);
        return timestamp;
    }

    private void EnsureActiveLease(string leaseOwner, DateTimeOffset atUtc)
    {
        if (Status != WorkflowRunStatus.Running)
        {
            throw new InvalidOperationException("The run is not running.");
        }

        var owner = DomainGuard.Require(
            leaseOwner,
            nameof(leaseOwner),
            MaximumLeaseOwnerLength);
        if (!string.Equals(LeaseOwner, owner, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The caller does not own the run lease.");
        }

        if (IsLeaseExpired(atUtc))
        {
            throw new InvalidOperationException("The run lease has expired.");
        }
    }

    private void TransitionToTerminal(
        WorkflowRunStatus terminalStatus,
        DateTimeOffset completedAtUtc,
        ExecutionError? error)
    {
        Status = terminalStatus;
        CompletedAtUtc = completedAtUtc;
        Error = error;
        ErrorMessage = error?.Summary;
        LeaseOwner = null;
        LeaseExpiresAtUtc = null;
        LastHeartbeatAtUtc = null;
    }
}
