using THub.Application.Alerts;
using THub.Application.Execution;
using THub.Application.Scheduling;
using THub.Domain.Runs;
using THub.Domain.Workflows;

namespace THub.Application.Workflows.Management;

public sealed class WorkflowRunService
{
    private readonly IWorkflowManagementRepository _repository;
    private readonly WorkflowInputValidator _inputValidator;
    private readonly WorkflowTerminalAlertService _terminalAlerts;
    private readonly TimeProvider _timeProvider;

    public WorkflowRunService(
        IWorkflowManagementRepository repository,
        WorkflowGraphSerializer graphSerializer,
        WorkflowGraphValidator graphValidator,
        ScheduleCalculator scheduleCalculator,
        WorkflowTerminalAlertService terminalAlerts,
        TimeProvider timeProvider,
        WorkflowNodeSettingsValidator? nodeSettingsValidator = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(graphSerializer);
        ArgumentNullException.ThrowIfNull(graphValidator);
        ArgumentNullException.ThrowIfNull(scheduleCalculator);
        ArgumentNullException.ThrowIfNull(terminalAlerts);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _repository = repository;
        _inputValidator = new(
            graphSerializer,
            graphValidator,
            scheduleCalculator,
            nodeSettingsValidator);
        _terminalAlerts = terminalAlerts;
        _timeProvider = timeProvider;
    }

    public async Task<WorkflowOperationResult<WorkflowRunDto>> QueueAsync(
        QueueWorkflowRunCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.WorkflowId == Guid.Empty)
        {
            return ValidationFailure(
                "workflow.id.required",
                "A workflow id is required.",
                nameof(command.WorkflowId));
        }

        if (string.IsNullOrWhiteSpace(command.TriggeredBy))
        {
            return ValidationFailure(
                "run.trigger.required",
                "A triggering identity is required.",
                nameof(command.TriggeredBy));
        }

        var workflow = await _repository.GetWorkflowAsync(
            command.WorkflowId,
            cancellationToken);
        if (workflow is null)
        {
            return Failure(
                WorkflowOperationStatus.NotFound,
                "workflow.not-found",
                "The workflow was not found.");
        }

        if (workflow.Status != WorkflowStatus.Published
            || workflow.PublishedVersionId is not { } publishedVersionId
            || workflow.PublishedVersionNumber is not { } publishedVersionNumber)
        {
            return Failure(
                WorkflowOperationStatus.InvalidState,
                "workflow.not-published",
                "Only a published workflow with an immutable version can be queued.");
        }

        var version = await _repository.GetWorkflowVersionAsync(
            publishedVersionId,
            cancellationToken);
        if (version is null)
        {
            return Failure(
                WorkflowOperationStatus.InvalidState,
                "workflow.version.missing",
                "The workflow's immutable published version was not found.");
        }

        var versionIssue = WorkflowCatalogService.ValidateVersion(workflow, version);
        if (versionIssue is not null)
        {
            return WorkflowOperationResult<WorkflowRunDto>.Failure(
                WorkflowOperationStatus.InvalidState,
                versionIssue);
        }

        var graph = _inputValidator.ValidateGraph(version.GraphJson);
        if (!graph.IsValid)
        {
            return WorkflowOperationResult<WorkflowRunDto>.Failure(
                WorkflowOperationStatus.InvalidState,
                graph.Issues
                    .Select(issue => issue with
                    {
                        Code = $"published.{issue.Code}",
                        Field = "workflowVersion.graphJson"
                    })
                    .ToArray());
        }

        WorkflowRun run;
        try
        {
            run = new WorkflowRun(
                workflow.Id,
                publishedVersionId,
                publishedVersionNumber,
                command.TriggeredBy,
                _timeProvider.GetUtcNow());
        }
        catch (ArgumentException exception)
        {
            return ValidationFailure(
                "run.value.invalid",
                exception.Message,
                exception.ParamName);
        }

        var write = await _repository.QueueRunAsync(
            run,
            publishedVersionId,
            cancellationToken);
        return MapWriteResult(write, run);
    }

    public async Task<WorkflowOperationResult<WorkflowRunDto>> CancelAsync(
        CancelWorkflowRunCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.RunId == Guid.Empty)
        {
            return ValidationFailure(
                "run.id.required",
                "A run id is required.",
                nameof(command.RunId));
        }

        if (string.IsNullOrWhiteSpace(command.RequestedBy))
        {
            return ValidationFailure(
                "run.cancellation.requester-required",
                "A cancellation requester identity is required.",
                nameof(command.RequestedBy));
        }

        var run = await _repository.GetRunAsync(command.RunId, cancellationToken);
        if (run is null)
        {
            return Failure(
                WorkflowOperationStatus.NotFound,
                "run.not-found",
                "The workflow run was not found.");
        }

        if (run.CancellationRequested)
        {
            return WorkflowOperationResult<WorkflowRunDto>.Success(MapRun(run));
        }

        if (run.IsTerminal)
        {
            return Failure(
                WorkflowOperationStatus.Conflict,
                "run.terminal",
                $"A run in the {run.Status} state cannot be cancelled.");
        }

        var expectedStatus = run.Status;
        try
        {
            _ = run.RequestCancellation(command.RequestedBy, _timeProvider.GetUtcNow());
        }
        catch (ArgumentException exception)
        {
            return ValidationFailure(
                "run.cancellation.invalid",
                exception.Message,
                exception.ParamName);
        }

        if (expectedStatus == WorkflowRunStatus.Queued)
        {
            var terminalCommit = await _terminalAlerts.CommitAsync(
                new CommitTerminalRunWithAlertsCommand(
                    run,
                    WorkflowRunStatus.Queued,
                    ExpectedLeaseOwner: null),
                cancellationToken);
            return MapTerminalCommitResult(terminalCommit, run);
        }

        var write = await _repository.SaveRunCancellationAsync(
            run,
            expectedStatus,
            cancellationToken);
        return MapWriteResult(write, run);
    }

    internal static WorkflowRunDto MapRun(WorkflowRun run) =>
        new(
            run.Id,
            run.WorkflowId,
            run.WorkflowVersionId,
            run.WorkflowVersion,
            run.Status,
            run.TriggeredBy,
            run.ScheduledForUtc,
            run.QueuedAtUtc,
            run.StartedAtUtc,
            run.CompletedAtUtc,
            run.CancellationRequestedAtUtc,
            run.CancellationRequestedBy);

    private static WorkflowOperationResult<WorkflowRunDto> MapWriteResult(
        WorkflowStoreWriteResult write,
        WorkflowRun run) => write.Status switch
        {
            WorkflowStoreWriteStatus.Succeeded =>
                WorkflowOperationResult<WorkflowRunDto>.Success(MapRun(run)),
            WorkflowStoreWriteStatus.NotFound => Failure(
                WorkflowOperationStatus.NotFound,
                write.Code ?? "resource.not-found",
                write.Message ?? "The requested resource was not found."),
            WorkflowStoreWriteStatus.ConcurrencyConflict => Failure(
                WorkflowOperationStatus.ConcurrencyConflict,
                write.Code ?? "run.concurrency",
                write.Message ?? "The run changed after it was loaded. Reload it and try again."),
            WorkflowStoreWriteStatus.Conflict => Failure(
                WorkflowOperationStatus.Conflict,
                write.Code ?? "run.conflict",
                write.Message ?? "The run conflicts with current workflow state."),
            _ => throw new InvalidOperationException(
                $"Unsupported workflow store result '{write.Status}'.")
        };

    private static WorkflowOperationResult<WorkflowRunDto> MapTerminalCommitResult(
        AlertResult<TerminalRunAlertCommitDto> result,
        WorkflowRun run)
    {
        if (result.IsSuccess)
        {
            return WorkflowOperationResult<WorkflowRunDto>.Success(MapRun(run));
        }

        var problem = result.Problem
            ?? new AlertProblem(
                "run.terminal-commit-failed",
                "The terminal run transition could not be committed.");
        var status = result.Status switch
        {
            AlertResultStatus.NotFound => WorkflowOperationStatus.NotFound,
            AlertResultStatus.ValidationFailed => WorkflowOperationStatus.InvalidState,
            AlertResultStatus.Conflict => WorkflowOperationStatus.ConcurrencyConflict,
            AlertResultStatus.Unavailable => WorkflowOperationStatus.Conflict,
            _ => WorkflowOperationStatus.Conflict
        };
        return WorkflowOperationResult<WorkflowRunDto>.Failure(
            status,
            new WorkflowIssue(problem.Code, problem.Message));
    }

    private static WorkflowOperationResult<WorkflowRunDto> ValidationFailure(
        string code,
        string message,
        string? field = null) => WorkflowOperationResult<WorkflowRunDto>.Failure(
            WorkflowOperationStatus.ValidationFailed,
            new WorkflowIssue(code, message, field));

    private static WorkflowOperationResult<WorkflowRunDto> Failure(
        WorkflowOperationStatus status,
        string code,
        string message) => WorkflowOperationResult<WorkflowRunDto>.Failure(
            status,
            new WorkflowIssue(code, message));
}
