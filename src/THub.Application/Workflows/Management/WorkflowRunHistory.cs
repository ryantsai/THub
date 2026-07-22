using THub.Domain.Runs;
using THub.Domain.Workflows;

namespace THub.Application.Workflows.Management;

public sealed record WorkflowRunListRequest(
    int Offset = 0,
    int Limit = 50,
    Guid? WorkflowId = null,
    WorkflowRunStatus? Status = null);

public sealed record WorkflowRunListFilter(
    int Offset,
    int Limit,
    Guid? WorkflowId,
    WorkflowRunStatus? Status);

public sealed record WorkflowRunListRecord(
    Guid Id,
    Guid WorkflowId,
    string WorkflowName,
    int WorkflowVersion,
    WorkflowRunStatus Status,
    string TriggeredBy,
    DateTimeOffset? ScheduledForUtc,
    DateTimeOffset QueuedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int AttemptCount,
    bool CancellationRequested,
    ExecutionError? Error);

public sealed record WorkflowRunListPage(
    IReadOnlyList<WorkflowRunListRecord> Items,
    int TotalCount);

public sealed record WorkflowStepRunDto(
    Guid Id,
    string NodeId,
    int Attempt,
    WorkflowStepRunStatus Status,
    DateTimeOffset QueuedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long RowsRead,
    long RowsWritten,
    long BatchesProcessed,
    long BytesRead,
    long BytesWritten,
    ExecutionError? Error);

public sealed record WorkflowRunDetailsDto(
    Guid Id,
    Guid WorkflowId,
    string WorkflowName,
    Guid WorkflowVersionId,
    int WorkflowVersion,
    Guid? RetryOfRunId,
    WorkflowRunStatus Status,
    string TriggeredBy,
    DateTimeOffset? ScheduledForUtc,
    DateTimeOffset QueuedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int AttemptCount,
    string? LeaseOwner,
    DateTimeOffset? LastHeartbeatAtUtc,
    DateTimeOffset? CancellationRequestedAtUtc,
    string? CancellationRequestedBy,
    ExecutionError? Error,
    IReadOnlyList<WorkflowStepRunDto> Steps);

public sealed record RetryWorkflowRunCommand(Guid RunId, string TriggeredBy);

public interface IWorkflowRunHistoryStore
{
    Task<WorkflowRunListPage> ListAsync(
        WorkflowRunListFilter filter,
        CancellationToken cancellationToken);

    Task<WorkflowRunDetailsDto?> GetDetailsAsync(
        Guid runId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically rechecks the failed source run and queues a retry against the same immutable
    /// workflow version. Implementations must enforce the active-run concurrency policy.
    /// </summary>
    Task<WorkflowStoreWriteResult> QueueRetryAsync(
        WorkflowRun retryRun,
        Guid sourceRunId,
        CancellationToken cancellationToken);
}

public sealed class WorkflowRunHistoryService(
    IWorkflowRunHistoryStore historyStore,
    IWorkflowManagementRepository workflowRepository,
    WorkflowGraphSerializer graphSerializer,
    WorkflowGraphValidator graphValidator,
    TimeProvider timeProvider)
{
    public async Task<WorkflowOperationResult<WorkflowRunListPage>> ListAsync(
        WorkflowRunListRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Offset < 0 || request.Limit is < 1 or > 200)
        {
            return Failure<WorkflowRunListPage>(
                WorkflowOperationStatus.ValidationFailed,
                "run.list.bounds",
                "Offset must be non-negative and limit must be between 1 and 200.");
        }

        if (request.WorkflowId == Guid.Empty)
        {
            return Failure<WorkflowRunListPage>(
                WorkflowOperationStatus.ValidationFailed,
                "workflow.id.required",
                "A workflow id must be non-empty when supplied.");
        }

        if (request.Status is { } status && !Enum.IsDefined(status))
        {
            return Failure<WorkflowRunListPage>(
                WorkflowOperationStatus.ValidationFailed,
                "run.status.invalid",
                "The run status filter is invalid.");
        }

        var page = await historyStore.ListAsync(
            new WorkflowRunListFilter(
                request.Offset,
                request.Limit,
                request.WorkflowId,
                request.Status),
            cancellationToken);
        return WorkflowOperationResult<WorkflowRunListPage>.Success(page);
    }

    public async Task<WorkflowOperationResult<WorkflowRunDetailsDto>> LoadAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty)
        {
            return Failure<WorkflowRunDetailsDto>(
                WorkflowOperationStatus.ValidationFailed,
                "run.id.required",
                "A run id is required.");
        }

        var run = await historyStore.GetDetailsAsync(runId, cancellationToken);
        return run is null
            ? Failure<WorkflowRunDetailsDto>(
                WorkflowOperationStatus.NotFound,
                "run.not-found",
                "The workflow run was not found.")
            : WorkflowOperationResult<WorkflowRunDetailsDto>.Success(run);
    }

    public async Task<WorkflowOperationResult<WorkflowRunDto>> RetryAsync(
        RetryWorkflowRunCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.RunId == Guid.Empty)
        {
            return Failure<WorkflowRunDto>(
                WorkflowOperationStatus.ValidationFailed,
                "run.id.required",
                "A run id is required.");
        }

        if (string.IsNullOrWhiteSpace(command.TriggeredBy))
        {
            return Failure<WorkflowRunDto>(
                WorkflowOperationStatus.ValidationFailed,
                "run.trigger.required",
                "A triggering identity is required.");
        }

        var source = await workflowRepository.GetRunAsync(command.RunId, cancellationToken);
        if (source is null)
        {
            return Failure<WorkflowRunDto>(
                WorkflowOperationStatus.NotFound,
                "run.not-found",
                "The workflow run was not found.");
        }

        if (source.Status != WorkflowRunStatus.Failed)
        {
            return Failure<WorkflowRunDto>(
                WorkflowOperationStatus.InvalidState,
                "run.retry.not-failed",
                "Only a failed workflow run can be retried.");
        }

        var version = await workflowRepository.GetWorkflowVersionAsync(
            source.WorkflowVersionId,
            cancellationToken);
        if (version is null
            || version.WorkflowId != source.WorkflowId
            || version.Version != source.WorkflowVersion
            || !string.Equals(
                version.Checksum,
                THub.Domain.Workflows.WorkflowVersion.ComputeChecksum(version.GraphJson),
                StringComparison.Ordinal))
        {
            return Failure<WorkflowRunDto>(
                WorkflowOperationStatus.InvalidState,
                "run.retry.version-invalid",
                "The immutable workflow version for this run is missing or invalid.");
        }

        WorkflowGraph graph;
        try
        {
            graph = graphSerializer.Deserialize(version.GraphJson);
        }
        catch (WorkflowGraphSerializationException)
        {
            return Failure<WorkflowRunDto>(
                WorkflowOperationStatus.InvalidState,
                "run.retry.graph-invalid",
                "The immutable workflow graph could not be loaded.");
        }

        if (graphValidator.Validate(graph).Count != 0)
        {
            return Failure<WorkflowRunDto>(
                WorkflowOperationStatus.InvalidState,
                "run.retry.graph-invalid",
                "The immutable workflow graph no longer passes execution validation.");
        }

        WorkflowRun retry;
        try
        {
            retry = new WorkflowRun(
                source.WorkflowId,
                source.WorkflowVersionId,
                source.WorkflowVersion,
                command.TriggeredBy,
                timeProvider.GetUtcNow(),
                retryOfRunId: source.Id);
        }
        catch (ArgumentException exception)
        {
            return Failure<WorkflowRunDto>(
                WorkflowOperationStatus.ValidationFailed,
                "run.retry.invalid",
                exception.Message);
        }

        var write = await historyStore.QueueRetryAsync(retry, source.Id, cancellationToken);
        return write.Status switch
        {
            WorkflowStoreWriteStatus.Succeeded =>
                WorkflowOperationResult<WorkflowRunDto>.Success(WorkflowRunService.MapRun(retry)),
            WorkflowStoreWriteStatus.NotFound => Failure<WorkflowRunDto>(
                WorkflowOperationStatus.NotFound,
                write.Code ?? "run.not-found",
                write.Message ?? "The source run was not found."),
            WorkflowStoreWriteStatus.ConcurrencyConflict => Failure<WorkflowRunDto>(
                WorkflowOperationStatus.ConcurrencyConflict,
                write.Code ?? "run.concurrency",
                write.Message ?? "The source run changed before its retry was queued."),
            WorkflowStoreWriteStatus.Conflict => Failure<WorkflowRunDto>(
                WorkflowOperationStatus.Conflict,
                write.Code ?? "run.retry.conflict",
                write.Message ?? "The retry conflicts with current workflow state."),
            _ => throw new InvalidOperationException($"Unsupported store status '{write.Status}'.")
        };
    }

    private static WorkflowOperationResult<T> Failure<T>(
        WorkflowOperationStatus status,
        string code,
        string message)
        where T : class => WorkflowOperationResult<T>.Failure(
            status,
            new WorkflowIssue(code, message));
}
