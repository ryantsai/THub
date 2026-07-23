using THub.Domain.Runs;
using THub.Domain.Workflows;

namespace THub.Application.Workflows.Management;

public sealed record WorkflowListRequest(
    int Offset = 0,
    int Limit = 50,
    string? Search = null,
    WorkflowStatus? Status = null);

public sealed record WorkflowListFilter(
    int Offset,
    int Limit,
    string? Search,
    WorkflowStatus? Status);

public sealed record WorkflowListRecord(
    Guid Id,
    string Name,
    string Description,
    string Owner,
    WorkflowStatus Status,
    int Version,
    int DraftRevision,
    int? PublishedVersionNumber,
    string? CronExpression,
    string TimeZoneId,
    DateTimeOffset? NextRunAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record WorkflowListPage(
    IReadOnlyList<WorkflowListRecord> Items,
    int TotalCount);

public sealed record WorkflowListItemDto(
    Guid Id,
    string Name,
    string Description,
    string Owner,
    WorkflowStatus Status,
    int Version,
    int DraftRevision,
    int? PublishedVersionNumber,
    string? CronExpression,
    string TimeZoneId,
    DateTimeOffset? NextRunAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record WorkflowListPageDto(
    IReadOnlyList<WorkflowListItemDto> Items,
    int TotalCount,
    int Offset,
    int Limit);

public sealed record WorkflowDetailsDto(
    Guid Id,
    string Name,
    string Description,
    string Owner,
    WorkflowStatus Status,
    int Version,
    int DraftRevision,
    string GraphJson,
    Guid? PublishedVersionId,
    int? PublishedVersionNumber,
    string? CronExpression,
    string TimeZoneId,
    DateTimeOffset? NextRunAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ArchivedAtUtc);

public sealed record WorkflowVersionDto(
    Guid Id,
    Guid WorkflowId,
    int Version,
    int SchemaVersion,
    string Checksum,
    string PublishedBy,
    DateTimeOffset PublishedAtUtc);

public sealed record PublishedWorkflowDto(
    WorkflowDetailsDto Workflow,
    WorkflowVersionDto Version,
    bool CreatedNewVersion);

public sealed record WorkflowRunDto(
    Guid Id,
    Guid WorkflowId,
    Guid WorkflowVersionId,
    int WorkflowVersion,
    WorkflowRunStatus Status,
    string TriggeredBy,
    DateTimeOffset? ScheduledForUtc,
    DateTimeOffset QueuedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? CancellationRequestedAtUtc,
    string? CancellationRequestedBy);

public sealed record CreateWorkflowCommand(
    string Name,
    string? Description,
    string Owner,
    string GraphJson,
    string? CronExpression = null,
    string TimeZoneId = "UTC");

public sealed record SaveWorkflowCommand(
    Guid WorkflowId,
    int ExpectedDraftRevision,
    string Name,
    string? Description,
    string Owner,
    string GraphJson,
    string? CronExpression,
    string TimeZoneId);

public sealed record PublishWorkflowCommand(
    Guid WorkflowId,
    int ExpectedDraftRevision,
    string PublishedBy);

public sealed record PauseWorkflowCommand(
    Guid WorkflowId,
    int ExpectedDraftRevision);

public sealed record ArchiveWorkflowCommand(
    Guid WorkflowId,
    int ExpectedDraftRevision);

public sealed record DeleteWorkflowCommand(
    Guid WorkflowId,
    int ExpectedDraftRevision);

public sealed record DeletedWorkflowDto(Guid Id);

public sealed record QueueWorkflowRunCommand(
    Guid WorkflowId,
    string TriggeredBy);

public sealed record CancelWorkflowRunCommand(
    Guid RunId,
    string RequestedBy);
