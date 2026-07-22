using THub.Domain.Runs;

namespace THub.Application.Execution;

/// <summary>
/// An authoritative immutable workflow-version snapshot claimed for execution.
/// </summary>
public sealed record WorkflowRunExecutionClaim(
    Guid WorkflowRunId,
    Guid WorkflowId,
    Guid WorkflowVersionId,
    int WorkflowVersion,
    int SchemaVersion,
    string GraphJson,
    string Checksum,
    DateTimeOffset LeaseExpiresAtUtc,
    bool CancellationRequested);

public enum WorkflowLeaseRenewalStatus
{
    Renewed,
    CancellationRequested,
    LeaseLost,
    NotFound
}

/// <summary>
/// Owns the SQL-authoritative claim, heartbeat, and cancellation observation boundary.
/// </summary>
public interface IWorkflowRunExecutionStore
{
    Task<WorkflowRunExecutionClaim?> TryClaimNextAsync(
        string leaseOwner,
        DateTimeOffset claimedAtUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<WorkflowLeaseRenewalStatus> RenewLeaseAsync(
        Guid workflowRunId,
        string leaseOwner,
        DateTimeOffset heartbeatAtUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    /// <summary>
    /// Loads a detached run only when the caller still owns its active lease. The returned entity
    /// can be transitioned to terminal state and passed to the terminal-alert commit service.
    /// </summary>
    Task<WorkflowRun?> LoadOwnedRunAsync(
        Guid workflowRunId,
        string leaseOwner,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken);
}

public interface IWorkflowExecutionEventSinkFactory
{
    IWorkflowExecutionEventSink Create(Guid workflowRunId, string leaseOwner);
}

/// <summary>
/// Resolves the durable step attempt currently executing a node. Actions use the durable identity
/// for deduplication instead of inventing an in-memory attempt identity.
/// </summary>
public interface IWorkflowStepRunLocator
{
    Task<Guid?> FindRunningStepIdAsync(
        Guid workflowRunId,
        string nodeId,
        CancellationToken cancellationToken);
}
