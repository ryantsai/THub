using THub.Domain.Publications;

namespace THub.Application.Publications;

public sealed record StagePublicationChangeCommand(
    PublicationChangeOperation Operation,
    string? KeyJson,
    string? BeforeJson,
    string? AfterJson);

public sealed record StagePublicationChangeSetCommand(
    Guid PublicationId,
    IReadOnlyCollection<Guid> RoleIds,
    IReadOnlyList<StagePublicationChangeCommand> Changes,
    string Actor);

public enum PublicationChangeReviewDecision
{
    Approve,
    Reject,
}

public sealed record ReviewPublicationChangeSetCommand(
    Guid PublicationId,
    Guid ChangeSetId,
    IReadOnlyCollection<Guid> RoleIds,
    PublicationChangeReviewDecision Decision,
    string? Comment,
    string Actor);

public sealed record PublicationChangeSetDto(
    Guid Id,
    Guid PublicationId,
    Guid PublicationVersionId,
    PublicationChangeSetStatus Status,
    int ChangeCount,
    string SubmittedBy,
    DateTimeOffset SubmittedAtUtc,
    string? ReviewedBy,
    DateTimeOffset? ReviewedAtUtc,
    string? ReviewComment,
    string? OutcomeDetail);

public enum PublicationChangeSetWriteStatus
{
    Saved,
    NotFound,
    Conflict,
}

public interface IPublicationChangeSetStore
{
    Task<PublicationChangeSet?> FindAsync(
        Guid publicationId,
        Guid changeSetId,
        CancellationToken cancellationToken);

    Task<PublicationChangeSetWriteStatus> AddAsync(
        PublicationChangeSet changeSet,
        string expectedGrantFingerprint,
        CancellationToken cancellationToken);

    Task<PublicationChangeSetWriteStatus> UpdateAsync(
        PublicationChangeSet changeSet,
        DateTimeOffset expectedUpdatedAtUtc,
        string expectedGrantFingerprint,
        CancellationToken cancellationToken);
}
