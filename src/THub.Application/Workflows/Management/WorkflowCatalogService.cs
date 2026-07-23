using THub.Application.Actions;
using THub.Application.Execution;
using THub.Application.Scheduling;
using THub.Domain.Workflows;

namespace THub.Application.Workflows.Management;

public sealed class WorkflowCatalogService
{
    public const int MaximumPageSize = 100;
    public const int MaximumSearchLength = 200;

    private readonly IWorkflowManagementRepository _repository;
    private readonly WorkflowInputValidator _inputValidator;
    private readonly TimeProvider _timeProvider;
    private readonly TrustedActionWorkflowAuthorization? _trustedActionAuthorization;

    public WorkflowCatalogService(
        IWorkflowManagementRepository repository,
        WorkflowGraphSerializer graphSerializer,
        WorkflowGraphValidator graphValidator,
        ScheduleCalculator scheduleCalculator,
        TimeProvider timeProvider,
        WorkflowNodeSettingsValidator? nodeSettingsValidator = null,
        TrustedActionWorkflowAuthorization? trustedActionAuthorization = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(graphSerializer);
        ArgumentNullException.ThrowIfNull(graphValidator);
        ArgumentNullException.ThrowIfNull(scheduleCalculator);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _repository = repository;
        _inputValidator = new(
            graphSerializer,
            graphValidator,
            scheduleCalculator,
            nodeSettingsValidator);
        _timeProvider = timeProvider;
        _trustedActionAuthorization = trustedActionAuthorization;
    }

    public async Task<WorkflowOperationResult<WorkflowListPageDto>> ListAsync(
        WorkflowListRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var issues = ValidateListRequest(request);
        if (issues.Count > 0)
        {
            return WorkflowOperationResult<WorkflowListPageDto>.Failure(
                WorkflowOperationStatus.ValidationFailed,
                issues);
        }

        var normalizedSearch = string.IsNullOrWhiteSpace(request.Search)
            ? null
            : request.Search.Trim();
        var page = await _repository.ListWorkflowsAsync(
            new WorkflowListFilter(
                request.Offset,
                request.Limit,
                normalizedSearch,
                request.Status),
            cancellationToken);

        if (page.TotalCount < 0 || page.Items.Count > request.Limit)
        {
            throw new InvalidOperationException(
                "The workflow repository returned an invalid bounded page.");
        }

        var items = page.Items.Select(MapListItem).ToArray();
        return WorkflowOperationResult<WorkflowListPageDto>.Success(
            new(items, page.TotalCount, request.Offset, request.Limit));
    }

    public async Task<WorkflowOperationResult<WorkflowDetailsDto>> LoadAsync(
        Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        if (workflowId == Guid.Empty)
        {
            return ValidationFailure<WorkflowDetailsDto>(
                "workflow.id.required",
                "A workflow id is required.",
                nameof(workflowId));
        }

        var workflow = await _repository.GetWorkflowAsync(workflowId, cancellationToken);
        if (workflow is null)
        {
            return NotFound<WorkflowDetailsDto>(
                "workflow.not-found",
                "The workflow was not found.");
        }

        var graph = _inputValidator.ValidateGraph(
            workflow.GraphJson,
            requirePublishableGraph: false);
        if (!graph.IsValid)
        {
            return WorkflowOperationResult<WorkflowDetailsDto>.Failure(
                WorkflowOperationStatus.InvalidState,
                graph.Issues
                    .Select(issue => issue with
                    {
                        Code = $"persisted.{issue.Code}",
                        Field = "workflow.graphJson"
                    })
                    .ToArray());
        }

        return WorkflowOperationResult<WorkflowDetailsDto>.Success(MapDetails(workflow));
    }

    public async Task<WorkflowOperationResult<WorkflowDetailsDto>> CreateAsync(
        CreateWorkflowCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = _timeProvider.GetUtcNow();
        var graph = _inputValidator.ValidateGraph(
            command.GraphJson,
            requirePublishableGraph: false);
        if (!graph.IsValid)
        {
            return WorkflowOperationResult<WorkflowDetailsDto>.Failure(
                WorkflowOperationStatus.ValidationFailed,
                graph.Issues);
        }

        var schedule = _inputValidator.ValidateSchedule(
            command.CronExpression,
            command.TimeZoneId,
            now);
        if (!schedule.IsValid)
        {
            return WorkflowOperationResult<WorkflowDetailsDto>.Failure(
                WorkflowOperationStatus.ValidationFailed,
                schedule.Issues);
        }

        WorkflowDefinition workflow;
        try
        {
            workflow = new WorkflowDefinition(
                command.Name,
                command.Owner,
                graph.CanonicalJson!,
                now,
                command.Description);
            workflow.SetSchedule(
                schedule.CronExpression,
                schedule.TimeZoneId!,
                nextRunAtUtc: null,
                now);
        }
        catch (ArgumentException exception)
        {
            return DomainValidationFailure<WorkflowDetailsDto>(exception);
        }
        catch (InvalidOperationException exception)
        {
            return InvalidState<WorkflowDetailsDto>("workflow.create.invalid-state", exception.Message);
        }

        var write = await _repository.CreateWorkflowAsync(workflow, cancellationToken);
        return MapWriteResult(write, MapDetails(workflow), "workflow.create.conflict");
    }

    public async Task<WorkflowOperationResult<WorkflowDetailsDto>> SaveAsync(
        SaveWorkflowCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var commandIssue = ValidateWorkflowCommandIdentity(
            command.WorkflowId,
            command.ExpectedDraftRevision);
        if (commandIssue is not null)
        {
            return WorkflowOperationResult<WorkflowDetailsDto>.Failure(
                WorkflowOperationStatus.ValidationFailed,
                commandIssue);
        }

        var graph = _inputValidator.ValidateGraph(
            command.GraphJson,
            requirePublishableGraph: false);
        if (!graph.IsValid)
        {
            return WorkflowOperationResult<WorkflowDetailsDto>.Failure(
                WorkflowOperationStatus.ValidationFailed,
                graph.Issues);
        }

        var now = _timeProvider.GetUtcNow();
        var schedule = _inputValidator.ValidateSchedule(
            command.CronExpression,
            command.TimeZoneId,
            now);
        if (!schedule.IsValid)
        {
            return WorkflowOperationResult<WorkflowDetailsDto>.Failure(
                WorkflowOperationStatus.ValidationFailed,
                schedule.Issues);
        }

        var workflow = await _repository.GetWorkflowAsync(
            command.WorkflowId,
            cancellationToken);
        if (workflow is null)
        {
            return NotFound<WorkflowDetailsDto>(
                "workflow.not-found",
                "The workflow was not found.");
        }

        if (workflow.DraftRevision != command.ExpectedDraftRevision)
        {
            return ConcurrencyFailure<WorkflowDetailsDto>(workflow.DraftRevision);
        }

        if (workflow.Status == WorkflowStatus.Archived)
        {
            return InvalidState<WorkflowDetailsDto>(
                "workflow.archived",
                "An archived workflow cannot be changed.");
        }

        try
        {
            workflow.Rename(command.Name, now);
            workflow.SetDescription(command.Description, now);
            workflow.TransferOwnership(command.Owner, now);
            workflow.UpdateGraph(graph.CanonicalJson!, now);

            var nextOccurrence = workflow.Status == WorkflowStatus.Published
                ? schedule.NextOccurrenceUtc
                : null;
            workflow.SetSchedule(
                schedule.CronExpression,
                schedule.TimeZoneId!,
                nextOccurrence,
                now);
            workflow.CompleteSave(command.ExpectedDraftRevision, now);
        }
        catch (ArgumentException exception)
        {
            return DomainValidationFailure<WorkflowDetailsDto>(exception);
        }
        catch (InvalidOperationException exception)
        {
            return InvalidState<WorkflowDetailsDto>("workflow.save.invalid-state", exception.Message);
        }
        catch (OverflowException)
        {
            return InvalidState<WorkflowDetailsDto>(
                "workflow.revision.exhausted",
                "The workflow revision limit has been reached.");
        }

        var write = await _repository.SaveWorkflowAsync(
            workflow,
            command.ExpectedDraftRevision,
            cancellationToken);
        return MapWriteResult(write, MapDetails(workflow), "workflow.save.conflict");
    }

    public async Task<WorkflowOperationResult<PublishedWorkflowDto>> PublishAsync(
        PublishWorkflowCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var commandIssue = ValidateWorkflowCommandIdentity(
            command.WorkflowId,
            command.ExpectedDraftRevision);
        if (commandIssue is not null)
        {
            return WorkflowOperationResult<PublishedWorkflowDto>.Failure(
                WorkflowOperationStatus.ValidationFailed,
                commandIssue);
        }

        if (string.IsNullOrWhiteSpace(command.PublishedBy))
        {
            return ValidationFailure<PublishedWorkflowDto>(
                "workflow.publisher.required",
                "A publisher identity is required.",
                nameof(command.PublishedBy));
        }

        var workflow = await _repository.GetWorkflowAsync(
            command.WorkflowId,
            cancellationToken);
        if (workflow is null)
        {
            return NotFound<PublishedWorkflowDto>(
                "workflow.not-found",
                "The workflow was not found.");
        }

        if (workflow.DraftRevision != command.ExpectedDraftRevision)
        {
            return ConcurrencyFailure<PublishedWorkflowDto>(workflow.DraftRevision);
        }

        if (workflow.Status is not (WorkflowStatus.Draft or WorkflowStatus.Paused))
        {
            return InvalidState<PublishedWorkflowDto>(
                "workflow.publish.invalid-state",
                $"A workflow in the {workflow.Status} state cannot be published.");
        }

        var graph = _inputValidator.ValidateGraph(workflow.GraphJson);
        if (!graph.IsValid)
        {
            return WorkflowOperationResult<PublishedWorkflowDto>.Failure(
                WorkflowOperationStatus.ValidationFailed,
                graph.Issues);
        }

        if (_trustedActionAuthorization is not null)
        {
            var actionIssues = await _trustedActionAuthorization.ValidatePublishAsync(
                graph.Graph!,
                command.AuthorizedRoleIds ?? new HashSet<Guid>(),
                cancellationToken).ConfigureAwait(false);
            if (actionIssues.Count > 0)
            {
                return WorkflowOperationResult<PublishedWorkflowDto>.Failure(
                    WorkflowOperationStatus.ValidationFailed,
                    actionIssues.Select(issue => new WorkflowIssue(
                        issue.Code,
                        issue.Message,
                        "workflow.graphJson",
                        issue.NodeId)).ToArray());
            }
        }

        var now = _timeProvider.GetUtcNow();
        var schedule = _inputValidator.ValidateSchedule(
            workflow.CronExpression,
            workflow.TimeZoneId,
            now);
        if (!schedule.IsValid)
        {
            return WorkflowOperationResult<PublishedWorkflowDto>.Failure(
                WorkflowOperationStatus.ValidationFailed,
                schedule.Issues);
        }

        if (workflow.Status == WorkflowStatus.Paused)
        {
            return await ResumeAsync(
                workflow,
                command.ExpectedDraftRevision,
                schedule.NextOccurrenceUtc,
                now,
                cancellationToken);
        }

        WorkflowVersion version;
        try
        {
            if (!string.Equals(
                    workflow.GraphJson,
                    graph.CanonicalJson,
                    StringComparison.Ordinal))
            {
                workflow.UpdateGraph(graph.CanonicalJson!, now);
            }

            version = new WorkflowVersion(
                workflow.Id,
                workflow.Version,
                WorkflowGraphSerializer.CurrentSchemaVersion,
                workflow.GraphJson,
                WorkflowVersion.ComputeChecksum(workflow.GraphJson),
                command.PublishedBy,
                now);
            workflow.Publish(version, schedule.NextOccurrenceUtc, now);
        }
        catch (ArgumentException exception)
        {
            return DomainValidationFailure<PublishedWorkflowDto>(exception);
        }
        catch (InvalidOperationException exception)
        {
            return InvalidState<PublishedWorkflowDto>(
                "workflow.publish.invalid-state",
                exception.Message);
        }
        catch (OverflowException)
        {
            return InvalidState<PublishedWorkflowDto>(
                "workflow.revision.exhausted",
                "The workflow revision limit has been reached.");
        }

        var write = await _repository.PublishWorkflowAsync(
            workflow,
            version,
            command.ExpectedDraftRevision,
            cancellationToken);
        var value = new PublishedWorkflowDto(
            MapDetails(workflow),
            MapVersion(version),
            CreatedNewVersion: true);
        return MapWriteResult(write, value, "workflow.publish.conflict");
    }

    public async Task<WorkflowOperationResult<WorkflowDetailsDto>> PauseAsync(
        PauseWorkflowCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var commandIssue = ValidateWorkflowCommandIdentity(
            command.WorkflowId,
            command.ExpectedDraftRevision);
        if (commandIssue is not null)
        {
            return WorkflowOperationResult<WorkflowDetailsDto>.Failure(
                WorkflowOperationStatus.ValidationFailed,
                commandIssue);
        }

        var workflow = await _repository.GetWorkflowAsync(command.WorkflowId, cancellationToken);
        if (workflow is null)
        {
            return NotFound<WorkflowDetailsDto>(
                "workflow.not-found",
                "The workflow was not found.");
        }

        if (workflow.DraftRevision != command.ExpectedDraftRevision)
        {
            return ConcurrencyFailure<WorkflowDetailsDto>(workflow.DraftRevision);
        }

        try
        {
            workflow.Pause(_timeProvider.GetUtcNow());
        }
        catch (InvalidOperationException exception)
        {
            return InvalidState<WorkflowDetailsDto>(
                "workflow.pause.invalid-state",
                exception.Message);
        }

        var write = await _repository.SaveWorkflowAsync(
            workflow,
            command.ExpectedDraftRevision,
            cancellationToken);
        return MapWriteResult(write, MapDetails(workflow), "workflow.pause.conflict");
    }

    public async Task<WorkflowOperationResult<WorkflowDetailsDto>> ArchiveAsync(
        ArchiveWorkflowCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var commandIssue = ValidateWorkflowCommandIdentity(
            command.WorkflowId,
            command.ExpectedDraftRevision);
        if (commandIssue is not null)
        {
            return WorkflowOperationResult<WorkflowDetailsDto>.Failure(
                WorkflowOperationStatus.ValidationFailed,
                commandIssue);
        }

        var workflow = await _repository.GetWorkflowAsync(command.WorkflowId, cancellationToken);
        if (workflow is null)
        {
            return NotFound<WorkflowDetailsDto>(
                "workflow.not-found",
                "The workflow was not found.");
        }

        if (workflow.DraftRevision != command.ExpectedDraftRevision)
        {
            return ConcurrencyFailure<WorkflowDetailsDto>(workflow.DraftRevision);
        }

        try
        {
            workflow.Archive(_timeProvider.GetUtcNow());
        }
        catch (InvalidOperationException exception)
        {
            return InvalidState<WorkflowDetailsDto>(
                "workflow.archive.invalid-state",
                exception.Message);
        }

        var write = await _repository.SaveWorkflowAsync(
            workflow,
            command.ExpectedDraftRevision,
            cancellationToken);
        return MapWriteResult(write, MapDetails(workflow), "workflow.archive.conflict");
    }

    public async Task<WorkflowOperationResult<DeletedWorkflowDto>> DeleteAsync(
        DeleteWorkflowCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var commandIssue = ValidateWorkflowCommandIdentity(
            command.WorkflowId,
            command.ExpectedDraftRevision);
        if (commandIssue is not null)
        {
            return WorkflowOperationResult<DeletedWorkflowDto>.Failure(
                WorkflowOperationStatus.ValidationFailed,
                commandIssue);
        }

        var workflow = await _repository.GetWorkflowAsync(command.WorkflowId, cancellationToken);
        if (workflow is null)
        {
            return NotFound<DeletedWorkflowDto>("workflow.not-found", "The workflow was not found.");
        }
        if (workflow.DraftRevision != command.ExpectedDraftRevision)
        {
            return ConcurrencyFailure<DeletedWorkflowDto>(workflow.DraftRevision);
        }
        if (workflow.Status != WorkflowStatus.Draft
            || workflow.PublishedVersionId is not null
            || workflow.PublishedVersionNumber is not null)
        {
            return InvalidState<DeletedWorkflowDto>(
                "workflow.delete.requires-unused-draft",
                "Only an unpublished draft with no published history can be permanently deleted. Archive this workflow instead.");
        }

        var write = await _repository.DeleteWorkflowAsync(
            workflow.Id,
            command.ExpectedDraftRevision,
            cancellationToken);
        return write.Status switch
        {
            WorkflowStoreWriteStatus.Succeeded =>
                WorkflowOperationResult<DeletedWorkflowDto>.Success(new(workflow.Id)),
            WorkflowStoreWriteStatus.NotFound =>
                NotFound<DeletedWorkflowDto>("workflow.not-found", "The workflow was not found."),
            WorkflowStoreWriteStatus.ConcurrencyConflict =>
                ConcurrencyFailure<DeletedWorkflowDto>(write.CurrentDraftRevision),
            _ => WorkflowOperationResult<DeletedWorkflowDto>.Failure(
                WorkflowOperationStatus.Conflict,
                new WorkflowIssue(
                    write.Code ?? "workflow.delete.conflict",
                    write.Message ?? "The workflow could not be permanently deleted."))
        };
    }

    private async Task<WorkflowOperationResult<PublishedWorkflowDto>> ResumeAsync(
        WorkflowDefinition workflow,
        int expectedDraftRevision,
        DateTimeOffset? nextOccurrenceUtc,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (workflow.PublishedVersionId is not { } publishedVersionId)
        {
            return InvalidState<PublishedWorkflowDto>(
                "workflow.version.missing",
                "The paused workflow does not reference an immutable published version.");
        }

        var version = await _repository.GetWorkflowVersionAsync(
            publishedVersionId,
            cancellationToken);
        if (version is null)
        {
            return InvalidState<PublishedWorkflowDto>(
                "workflow.version.missing",
                "The paused workflow's immutable version was not found.");
        }

        var versionIssue = ValidateVersion(workflow, version);
        if (versionIssue is not null)
        {
            return WorkflowOperationResult<PublishedWorkflowDto>.Failure(
                WorkflowOperationStatus.InvalidState,
                versionIssue);
        }

        var versionGraph = _inputValidator.ValidateGraph(version.GraphJson);
        if (!versionGraph.IsValid)
        {
            return WorkflowOperationResult<PublishedWorkflowDto>.Failure(
                WorkflowOperationStatus.InvalidState,
                versionGraph.Issues
                    .Select(issue => issue with
                    {
                        Code = $"published.{issue.Code}",
                        Field = "workflowVersion.graphJson"
                    })
                    .ToArray());
        }

        try
        {
            workflow.Publish(version, nextOccurrenceUtc, now);
        }
        catch (ArgumentException exception)
        {
            return DomainValidationFailure<PublishedWorkflowDto>(exception);
        }
        catch (InvalidOperationException exception)
        {
            return InvalidState<PublishedWorkflowDto>(
                "workflow.resume.invalid-state",
                exception.Message);
        }

        var write = await _repository.ResumeWorkflowAsync(
            workflow,
            expectedDraftRevision,
            cancellationToken);
        var value = new PublishedWorkflowDto(
            MapDetails(workflow),
            MapVersion(version),
            CreatedNewVersion: false);
        return MapWriteResult(write, value, "workflow.resume.conflict");
    }

    internal static WorkflowIssue? ValidateVersion(
        WorkflowDefinition workflow,
        WorkflowVersion version)
    {
        if (version.Id != workflow.PublishedVersionId
            || version.WorkflowId != workflow.Id
            || version.Version != workflow.PublishedVersionNumber
            || !string.Equals(
                version.GraphJson,
                workflow.GraphJson,
                StringComparison.Ordinal))
        {
            return new(
                "workflow.version.mismatch",
                "The immutable version does not match the workflow publication metadata.");
        }

        if (version.SchemaVersion != WorkflowGraphSerializer.CurrentSchemaVersion)
        {
            return new(
                "workflow.version.schema-unsupported",
                $"Workflow graph schema version {version.SchemaVersion} is not supported.");
        }

        string expectedChecksum;
        try
        {
            expectedChecksum = WorkflowVersion.ComputeChecksum(version.GraphJson);
        }
        catch (ArgumentException)
        {
            return new(
                "workflow.version.graph-invalid",
                "The immutable workflow version graph is missing or exceeds its size bound.");
        }

        if (!string.Equals(
                version.Checksum,
                expectedChecksum,
                StringComparison.OrdinalIgnoreCase))
        {
            return new(
                "workflow.version.checksum-invalid",
                "The immutable workflow version checksum is invalid.");
        }

        return null;
    }

    internal static WorkflowDetailsDto MapDetails(WorkflowDefinition workflow) =>
        new(
            workflow.Id,
            workflow.Name,
            workflow.Description,
            workflow.Owner,
            workflow.Status,
            workflow.Version,
            workflow.DraftRevision,
            workflow.GraphJson,
            workflow.PublishedVersionId,
            workflow.PublishedVersionNumber,
            workflow.CronExpression,
            workflow.TimeZoneId,
            workflow.NextRunAtUtc,
            workflow.CreatedAtUtc,
            workflow.UpdatedAtUtc,
            workflow.ArchivedAtUtc);

    internal static WorkflowVersionDto MapVersion(WorkflowVersion version) =>
        new(
            version.Id,
            version.WorkflowId,
            version.Version,
            version.SchemaVersion,
            version.Checksum,
            version.PublishedBy,
            version.PublishedAtUtc);

    private static WorkflowListItemDto MapListItem(WorkflowListRecord item) =>
        new(
            item.Id,
            item.Name,
            item.Description,
            item.Owner,
            item.Status,
            item.Version,
            item.DraftRevision,
            item.PublishedVersionNumber,
            item.CronExpression,
            item.TimeZoneId,
            item.NextRunAtUtc,
            item.UpdatedAtUtc);

    private static List<WorkflowIssue> ValidateListRequest(WorkflowListRequest request)
    {
        var issues = new List<WorkflowIssue>();
        if (request.Offset < 0)
        {
            issues.Add(new(
                "workflow.list.offset",
                "The list offset cannot be negative.",
                nameof(request.Offset)));
        }

        if (request.Limit is < 1 or > MaximumPageSize)
        {
            issues.Add(new(
                "workflow.list.limit",
                $"The page size must be between 1 and {MaximumPageSize}.",
                nameof(request.Limit)));
        }

        if (request.Search?.Trim().Length > MaximumSearchLength)
        {
            issues.Add(new(
                "workflow.list.search-length",
                $"The search text cannot exceed {MaximumSearchLength} characters.",
                nameof(request.Search)));
        }

        if (request.Status is { } status && !Enum.IsDefined(status))
        {
            issues.Add(new(
                "workflow.list.status",
                "The workflow status filter is not supported.",
                nameof(request.Status)));
        }

        return issues;
    }

    private static WorkflowIssue? ValidateWorkflowCommandIdentity(
        Guid workflowId,
        int expectedDraftRevision)
    {
        if (workflowId == Guid.Empty)
        {
            return new(
                "workflow.id.required",
                "A workflow id is required.",
                nameof(workflowId));
        }

        return expectedDraftRevision <= 0
            ? new(
                "workflow.revision.required",
                "A positive expected draft revision is required.",
                nameof(expectedDraftRevision))
            : null;
    }

    private static WorkflowOperationResult<T> MapWriteResult<T>(
        WorkflowStoreWriteResult write,
        T value,
        string defaultConflictCode)
        where T : class => write.Status switch
        {
            WorkflowStoreWriteStatus.Succeeded => WorkflowOperationResult<T>.Success(value),
            WorkflowStoreWriteStatus.NotFound => NotFound<T>(
                write.Code ?? "resource.not-found",
                write.Message ?? "The requested resource was not found."),
            WorkflowStoreWriteStatus.ConcurrencyConflict =>
                ConcurrencyFailure<T>(write.CurrentDraftRevision),
            WorkflowStoreWriteStatus.Conflict => WorkflowOperationResult<T>.Failure(
                WorkflowOperationStatus.Conflict,
                new WorkflowIssue(
                    write.Code ?? defaultConflictCode,
                    write.Message ?? "The operation conflicts with current workflow state.")),
            _ => throw new InvalidOperationException(
                $"Unsupported workflow store result '{write.Status}'.")
        };

    private static WorkflowOperationResult<T> ValidationFailure<T>(
        string code,
        string message,
        string? field = null)
        where T : class => WorkflowOperationResult<T>.Failure(
            WorkflowOperationStatus.ValidationFailed,
            new WorkflowIssue(code, message, field));

    private static WorkflowOperationResult<T> DomainValidationFailure<T>(
        ArgumentException exception)
        where T : class => ValidationFailure<T>(
            "workflow.value.invalid",
            exception.Message,
            exception.ParamName);

    private static WorkflowOperationResult<T> NotFound<T>(string code, string message)
        where T : class => WorkflowOperationResult<T>.Failure(
            WorkflowOperationStatus.NotFound,
            new WorkflowIssue(code, message));

    private static WorkflowOperationResult<T> ConcurrencyFailure<T>(int? currentRevision)
        where T : class => WorkflowOperationResult<T>.Failure(
            WorkflowOperationStatus.ConcurrencyConflict,
            new WorkflowIssue(
                "workflow.concurrency",
                currentRevision is null
                    ? "The workflow changed after it was loaded. Reload it and try again."
                    : $"The workflow changed after it was loaded. Its current draft revision is {currentRevision}. Reload it and try again.",
                "expectedDraftRevision"));

    private static WorkflowOperationResult<T> InvalidState<T>(string code, string message)
        where T : class => WorkflowOperationResult<T>.Failure(
            WorkflowOperationStatus.InvalidState,
            new WorkflowIssue(code, message));
}
