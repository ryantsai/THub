using System.Collections.ObjectModel;

namespace THub.Domain.Publications;

public sealed class PublicationChangeSet
{
    public const int MaximumChanges = 1_000;
    public const int MaximumCommentLength = 2_000;
    public const string AuthorizationChangedOutcome =
        "Publication role grants changed before apply; review and submit the changes again.";

    private readonly List<PublicationChange> _changes = [];

    private PublicationChangeSet()
    {
    }

    public PublicationChangeSet(
        Guid id,
        Guid publicationId,
        Guid publicationVersionId,
        IEnumerable<PublicationChange> changes,
        string submittedBy,
        DateTimeOffset submittedAtUtc)
    {
        Id = PublicationGuard.RequireId(id, nameof(id));
        PublicationId = PublicationGuard.RequireId(publicationId, nameof(publicationId));
        PublicationVersionId = PublicationGuard.RequireId(publicationVersionId, nameof(publicationVersionId));
        SubmittedBy = PublicationGuard.Require(
            submittedBy,
            nameof(submittedBy),
            Publication.MaximumIdentityLength);
        SubmittedAtUtc = PublicationGuard.AsUtc(submittedAtUtc);
        UpdatedAtUtc = SubmittedAtUtc;

        ArgumentNullException.ThrowIfNull(changes);
        _changes.AddRange(changes);
        if (_changes.Count is < 1 or > MaximumChanges)
        {
            throw new ArgumentOutOfRangeException(
                nameof(changes),
                $"A change set must contain between 1 and {MaximumChanges} changes.");
        }

        if (_changes.Any(change => change.ChangeSetId != Id))
        {
            throw new ArgumentException("Every change must belong to this change set.", nameof(changes));
        }

        if (_changes.Select(change => change.Id).Distinct().Count() != _changes.Count)
        {
            throw new ArgumentException("Change identifiers must be unique within a change set.", nameof(changes));
        }
    }

    public Guid Id { get; private set; }

    public Guid PublicationId { get; private set; }

    public Guid PublicationVersionId { get; private set; }

    public PublicationChangeSetStatus Status { get; private set; } = PublicationChangeSetStatus.Pending;

    public ReadOnlyCollection<PublicationChange> Changes => _changes.AsReadOnly();

    public string SubmittedBy { get; private set; } = string.Empty;

    public DateTimeOffset SubmittedAtUtc { get; private set; }

    public string? ReviewedBy { get; private set; }

    public DateTimeOffset? ReviewedAtUtc { get; private set; }

    public string? ReviewComment { get; private set; }

    public string? ApplyStartedBy { get; private set; }

    public DateTimeOffset? ApplyStartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public string? OutcomeDetail { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void Approve(string reviewedBy, DateTimeOffset reviewedAtUtc, string? comment = null)
    {
        EnsureStatus(PublicationChangeSetStatus.Pending);
        SetReview(reviewedBy, reviewedAtUtc, comment);
        Status = PublicationChangeSetStatus.Approved;
    }

    public void Reject(string reviewedBy, string reason, DateTimeOffset reviewedAtUtc)
    {
        EnsureStatus(PublicationChangeSetStatus.Pending);
        SetReview(
            reviewedBy,
            reviewedAtUtc,
            PublicationGuard.Require(reason, nameof(reason), MaximumCommentLength));
        Status = PublicationChangeSetStatus.Rejected;
        CompletedAtUtc = ReviewedAtUtc;
    }

    public void InvalidateAuthorization(DateTimeOffset invalidatedAtUtc)
    {
        if (Status is not PublicationChangeSetStatus.Pending and
            not PublicationChangeSetStatus.Approved)
        {
            throw new InvalidOperationException(
                "Only pending or approved change sets can be invalidated by an authorization change.");
        }

        CompletedAtUtc = Advance(invalidatedAtUtc, nameof(invalidatedAtUtc));
        OutcomeDetail = AuthorizationChangedOutcome;
        Status = PublicationChangeSetStatus.Rejected;
    }

    public void BeginApplying(string applyStartedBy, DateTimeOffset applyStartedAtUtc)
    {
        EnsureStatus(PublicationChangeSetStatus.Approved);
        ApplyStartedBy = PublicationGuard.Require(
            applyStartedBy,
            nameof(applyStartedBy),
            Publication.MaximumIdentityLength);
        ApplyStartedAtUtc = Advance(applyStartedAtUtc, nameof(applyStartedAtUtc));
        Status = PublicationChangeSetStatus.Applying;
    }

    public void MarkApplied(DateTimeOffset completedAtUtc)
    {
        Complete(PublicationChangeSetStatus.Applied, completedAtUtc, null);
    }

    public void MarkConflict(string detail, DateTimeOffset completedAtUtc)
    {
        Complete(
            PublicationChangeSetStatus.Conflict,
            completedAtUtc,
            PublicationGuard.Require(detail, nameof(detail), MaximumCommentLength));
    }

    public void MarkFailed(string detail, DateTimeOffset completedAtUtc)
    {
        Complete(
            PublicationChangeSetStatus.Failed,
            completedAtUtc,
            PublicationGuard.Require(detail, nameof(detail), MaximumCommentLength));
    }

    private void SetReview(string reviewedBy, DateTimeOffset reviewedAtUtc, string? comment)
    {
        ReviewedBy = PublicationGuard.Require(
            reviewedBy,
            nameof(reviewedBy),
            Publication.MaximumIdentityLength);
        ReviewedAtUtc = Advance(reviewedAtUtc, nameof(reviewedAtUtc));
        ReviewComment = PublicationGuard.Optional(comment, nameof(comment), MaximumCommentLength);
    }

    private void Complete(
        PublicationChangeSetStatus completedStatus,
        DateTimeOffset completedAtUtc,
        string? outcomeDetail)
    {
        EnsureStatus(PublicationChangeSetStatus.Applying);
        CompletedAtUtc = Advance(completedAtUtc, nameof(completedAtUtc));
        OutcomeDetail = outcomeDetail;
        Status = completedStatus;
    }

    private DateTimeOffset Advance(DateTimeOffset timestamp, string parameterName)
    {
        UpdatedAtUtc = PublicationGuard.NotBefore(timestamp, UpdatedAtUtc, parameterName);
        return UpdatedAtUtc;
    }

    private void EnsureStatus(PublicationChangeSetStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException(
                $"Change set must be {expected} for this transition; current state is {Status}.");
        }
    }
}
