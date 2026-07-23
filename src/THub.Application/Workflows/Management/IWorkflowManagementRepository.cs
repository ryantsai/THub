using THub.Domain.Runs;
using THub.Domain.Workflows;

namespace THub.Application.Workflows.Management;

public enum WorkflowStoreWriteStatus
{
    Succeeded,
    NotFound,
    ConcurrencyConflict,
    Conflict
}

public sealed record WorkflowStoreWriteResult(
    WorkflowStoreWriteStatus Status,
    string? Code = null,
    string? Message = null,
    int? CurrentDraftRevision = null)
{
    public static WorkflowStoreWriteResult Success { get; } =
        new(WorkflowStoreWriteStatus.Succeeded);

    public static WorkflowStoreWriteResult NotFound(string message) =>
        new(WorkflowStoreWriteStatus.NotFound, "resource.not-found", message);

    public static WorkflowStoreWriteResult Concurrency(int? currentDraftRevision = null) =>
        new(
            WorkflowStoreWriteStatus.ConcurrencyConflict,
            "workflow.concurrency",
            "The workflow changed after it was loaded.",
            currentDraftRevision);

    public static WorkflowStoreWriteResult Conflict(string code, string message) =>
        new(WorkflowStoreWriteStatus.Conflict, code, message);
}

/// <summary>
/// Transactional persistence port for workflow management. Read methods must return
/// detached or otherwise isolated aggregates: callers can mutate a returned aggregate
/// without persisting anything until the matching write method succeeds.
/// </summary>
public interface IWorkflowManagementRepository
{
    Task<WorkflowListPage> ListWorkflowsAsync(
        WorkflowListFilter filter,
        CancellationToken cancellationToken);

    Task<WorkflowDefinition?> GetWorkflowAsync(
        Guid workflowId,
        CancellationToken cancellationToken);

    Task<WorkflowVersion?> GetWorkflowVersionAsync(
        Guid workflowVersionId,
        CancellationToken cancellationToken);

    Task<WorkflowRun?> GetRunAsync(
        Guid runId,
        CancellationToken cancellationToken);

    Task<WorkflowStoreWriteResult> CreateWorkflowAsync(
        WorkflowDefinition workflow,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically updates the workflow only when its persisted draft revision equals
    /// <paramref name="expectedDraftRevision"/>.
    /// </summary>
    Task<WorkflowStoreWriteResult> SaveWorkflowAsync(
        WorkflowDefinition workflow,
        int expectedDraftRevision,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically inserts the immutable version and updates its workflow, conditional
    /// on the persisted draft revision. Neither change may commit by itself.
    /// </summary>
    Task<WorkflowStoreWriteResult> PublishWorkflowAsync(
        WorkflowDefinition workflow,
        WorkflowVersion version,
        int expectedDraftRevision,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically resumes a paused workflow on its existing immutable version.
    /// </summary>
    Task<WorkflowStoreWriteResult> ResumeWorkflowAsync(
        WorkflowDefinition workflow,
        int expectedDraftRevision,
        CancellationToken cancellationToken);

    /// <summary>
    /// Permanently deletes an unpublished draft with no immutable versions, runs, or
    /// alert rules, conditional on its persisted draft revision.
    /// </summary>
    Task<WorkflowStoreWriteResult> DeleteWorkflowAsync(
        Guid workflowId,
        int expectedDraftRevision,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically verifies that the workflow is still published on
    /// <paramref name="expectedWorkflowVersionId"/> and queues the run. Implementations
    /// should also enforce the configured active-run concurrency policy.
    /// </summary>
    Task<WorkflowStoreWriteResult> QueueRunAsync(
        WorkflowRun run,
        Guid expectedWorkflowVersionId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically saves a cancellation request only if the persisted run status still
    /// equals <paramref name="expectedStatus"/>.
    /// </summary>
    Task<WorkflowStoreWriteResult> SaveRunCancellationAsync(
        WorkflowRun run,
        WorkflowRunStatus expectedStatus,
        CancellationToken cancellationToken);
}
