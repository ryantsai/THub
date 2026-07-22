using THub.Domain.Runs;

namespace THub.Domain.Alerts;

public enum AlertDeliverySource
{
    WorkflowRule,
    EmailAction
}

public enum AlertDeliveryEvent
{
    RunFailed,
    RunSucceeded,
    RunCancelled,
    EmailActionQueued
}

public enum AlertDeliveryStatus
{
    Pending,
    Sending,
    RetryScheduled,
    Delivered,
    DeadLettered
}

public sealed class AlertDelivery
{
    public const int AbsoluteMaximumAttempts = 20;
    public const int MaximumDeduplicationKeyLength = 500;
    public const int MaximumLeaseOwnerLength = 200;

    private static readonly TimeSpan MaximumLeaseDuration = TimeSpan.FromHours(1);

    private AlertDelivery() { }

    private AlertDelivery(
        Guid workflowRunId,
        Guid emailDeliveryProfileId,
        AlertDeliverySource source,
        AlertDeliveryEvent eventType,
        Guid? workflowAlertRuleId,
        Guid? workflowStepRunId,
        string? workflowNodeId,
        string deduplicationKey,
        EmailMessage message,
        DateTimeOffset createdAtUtc,
        int maximumAttempts)
    {
        if (maximumAttempts is < 1 or > AbsoluteMaximumAttempts)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumAttempts),
                $"Maximum attempts must be between 1 and {AbsoluteMaximumAttempts}.");
        }

        Id = Guid.NewGuid();
        WorkflowRunId = DomainGuard.RequireId(workflowRunId, nameof(workflowRunId));
        EmailDeliveryProfileId = DomainGuard.RequireId(
            emailDeliveryProfileId,
            nameof(emailDeliveryProfileId));
        Source = source;
        Event = eventType;
        WorkflowAlertRuleId = workflowAlertRuleId;
        WorkflowStepRunId = workflowStepRunId;
        WorkflowNodeId = workflowNodeId;
        DeduplicationKey = DomainGuard.Require(
            deduplicationKey,
            nameof(deduplicationKey),
            MaximumDeduplicationKeyLength);
        Message = message ?? throw new ArgumentNullException(nameof(message));
        MaximumAttempts = maximumAttempts;
        CreatedAtUtc = DomainGuard.Utc(createdAtUtc, nameof(createdAtUtc));
        NextAttemptAtUtc = CreatedAtUtc;
        StableMessageId = $"{Id:N}@thub";
    }

    public Guid Id { get; private set; }

    public Guid WorkflowRunId { get; private set; }

    public Guid EmailDeliveryProfileId { get; private set; }

    public AlertDeliverySource Source { get; private set; }

    public AlertDeliveryEvent Event { get; private set; }

    public Guid? WorkflowAlertRuleId { get; private set; }

    public Guid? WorkflowStepRunId { get; private set; }

    /// <summary>
    /// Stable graph-node identity for an Email action. Unlike a step-run identity, this remains
    /// unchanged when an abandoned run attempt is recovered with a new durable step attempt.
    /// </summary>
    public string? WorkflowNodeId { get; private set; }

    public string DeduplicationKey { get; private set; } = string.Empty;

    public string StableMessageId { get; private set; } = string.Empty;

    public EmailMessage Message { get; private set; } = null!;

    public AlertDeliveryStatus Status { get; private set; } = AlertDeliveryStatus.Pending;

    public int MaximumAttempts { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset NextAttemptAtUtc { get; private set; }

    public DateTimeOffset? LastAttemptAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public string? LeaseOwner { get; private set; }

    public DateTimeOffset? LeaseExpiresAtUtc { get; private set; }

    public DateTimeOffset? LastHeartbeatAtUtc { get; private set; }

    public ExecutionError? LastError { get; private set; }

    public string? ProviderMessageId { get; private set; }

    public bool IsTerminal =>
        Status is AlertDeliveryStatus.Delivered or AlertDeliveryStatus.DeadLettered;

    public static AlertDelivery ForWorkflowRule(
        Guid workflowAlertRuleId,
        Guid workflowRunId,
        Guid emailDeliveryProfileId,
        AlertDeliveryEvent eventType,
        EmailMessage message,
        DateTimeOffset createdAtUtc,
        int maximumAttempts = 5)
    {
        DomainGuard.RequireId(workflowAlertRuleId, nameof(workflowAlertRuleId));
        if (eventType == AlertDeliveryEvent.EmailActionQueued)
        {
            throw new ArgumentOutOfRangeException(
                nameof(eventType),
                "A workflow rule requires a terminal run event.");
        }

        var deduplicationKey =
            $"rule:{workflowAlertRuleId:N}:run:{workflowRunId:N}:event:{eventType}";
        return new AlertDelivery(
            workflowRunId,
            emailDeliveryProfileId,
            AlertDeliverySource.WorkflowRule,
            eventType,
            workflowAlertRuleId,
            workflowStepRunId: null,
            workflowNodeId: null,
            deduplicationKey,
            message,
            createdAtUtc,
            maximumAttempts);
    }

    public static AlertDelivery ForEmailAction(
        Guid workflowRunId,
        Guid workflowStepRunId,
        string workflowNodeId,
        Guid emailDeliveryProfileId,
        EmailMessage message,
        DateTimeOffset createdAtUtc,
        int maximumAttempts = 5)
    {
        DomainGuard.RequireId(workflowStepRunId, nameof(workflowStepRunId));
        var nodeId = DomainGuard.Require(
            workflowNodeId,
            nameof(workflowNodeId),
            WorkflowStepRun.MaximumNodeIdLength);
        var deduplicationKey =
            $"action:run:{workflowRunId:N}:node:{nodeId}";
        return new AlertDelivery(
            workflowRunId,
            emailDeliveryProfileId,
            AlertDeliverySource.EmailAction,
            AlertDeliveryEvent.EmailActionQueued,
            workflowAlertRuleId: null,
            workflowStepRunId,
            nodeId,
            deduplicationKey,
            message,
            createdAtUtc,
            maximumAttempts);
    }

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
            CreatedAtUtc,
            nameof(claimedAtUtc));
        var duration = DomainGuard.LeaseDuration(
            leaseDuration,
            nameof(leaseDuration),
            MaximumLeaseDuration);

        if (IsTerminal)
        {
            return false;
        }

        if (Status == AlertDeliveryStatus.Sending && !IsLeaseExpired(timestamp))
        {
            return false;
        }

        if (Status is AlertDeliveryStatus.Pending or AlertDeliveryStatus.RetryScheduled
            && NextAttemptAtUtc > timestamp)
        {
            return false;
        }

        if (AttemptCount >= MaximumAttempts)
        {
            DeadLetterForAttemptLimit(timestamp);
            return false;
        }

        Status = AlertDeliveryStatus.Sending;
        AttemptCount++;
        LastAttemptAtUtc = timestamp;
        LastHeartbeatAtUtc = timestamp;
        LeaseOwner = owner;
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
            LastHeartbeatAtUtc ?? CreatedAtUtc,
            nameof(heartbeatAtUtc));
        EnsureActiveLease(leaseOwner, timestamp);
        var duration = DomainGuard.LeaseDuration(
            leaseDuration,
            nameof(leaseDuration),
            MaximumLeaseDuration);

        LastHeartbeatAtUtc = timestamp;
        LeaseExpiresAtUtc = timestamp.Add(duration);
    }

    public void RecordDelivered(
        string leaseOwner,
        DateTimeOffset deliveredAtUtc,
        string? providerMessageId = null)
    {
        var timestamp = ValidateOwnedTransition(leaseOwner, deliveredAtUtc);
        ProviderMessageId = string.IsNullOrWhiteSpace(providerMessageId)
            ? null
            : DomainGuard.Require(providerMessageId, nameof(providerMessageId), 500);
        Status = AlertDeliveryStatus.Delivered;
        CompletedAtUtc = timestamp;
        ClearLease();
    }

    public void RecordFailure(
        string leaseOwner,
        ExecutionError error,
        DateTimeOffset failedAtUtc,
        DateTimeOffset? nextAttemptAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        var timestamp = ValidateOwnedTransition(leaseOwner, failedAtUtc);
        var canRetry = error.IsRetryable && AttemptCount < MaximumAttempts;

        if (canRetry && nextAttemptAtUtc is null)
        {
            throw new ArgumentNullException(
                nameof(nextAttemptAtUtc),
                "A retryable delivery failure requires a next-attempt timestamp.");
        }

        if (canRetry)
        {
            var retryAtUtc = DomainGuard.OnOrAfter(
                nextAttemptAtUtc!.Value,
                timestamp,
                nameof(nextAttemptAtUtc));
            if (retryAtUtc == timestamp)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(nextAttemptAtUtc),
                    "A retry must be scheduled after the failed attempt.");
            }

            Status = AlertDeliveryStatus.RetryScheduled;
            NextAttemptAtUtc = retryAtUtc;
        }
        else
        {
            Status = AlertDeliveryStatus.DeadLettered;
            CompletedAtUtc = timestamp;
        }

        LastError = error;
        ClearLease();
    }

    public bool IsLeaseExpired(DateTimeOffset atUtc)
    {
        var timestamp = DomainGuard.Utc(atUtc, nameof(atUtc));
        return LeaseExpiresAtUtc is null || LeaseExpiresAtUtc <= timestamp;
    }

    private DateTimeOffset ValidateOwnedTransition(
        string leaseOwner,
        DateTimeOffset occurredAtUtc)
    {
        var timestamp = DomainGuard.OnOrAfter(
            occurredAtUtc,
            LastHeartbeatAtUtc ?? CreatedAtUtc,
            nameof(occurredAtUtc));
        EnsureActiveLease(leaseOwner, timestamp);
        return timestamp;
    }

    private void EnsureActiveLease(string leaseOwner, DateTimeOffset atUtc)
    {
        if (Status != AlertDeliveryStatus.Sending)
        {
            throw new InvalidOperationException("The alert delivery is not being sent.");
        }

        var owner = DomainGuard.Require(
            leaseOwner,
            nameof(leaseOwner),
            MaximumLeaseOwnerLength);
        if (!string.Equals(LeaseOwner, owner, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The caller does not own the delivery lease.");
        }

        if (IsLeaseExpired(atUtc))
        {
            throw new InvalidOperationException("The delivery lease has expired.");
        }
    }

    private void DeadLetterForAttemptLimit(DateTimeOffset completedAtUtc)
    {
        Status = AlertDeliveryStatus.DeadLettered;
        CompletedAtUtc = completedAtUtc;
        LastError = new ExecutionError(
            "email.attempt_limit",
            ExecutionErrorCategory.ExternalSideEffect,
            "The Email delivery attempt limit was reached.",
            isRetryable: false);
        ClearLease();
    }

    private void ClearLease()
    {
        LeaseOwner = null;
        LeaseExpiresAtUtc = null;
        LastHeartbeatAtUtc = null;
    }
}
