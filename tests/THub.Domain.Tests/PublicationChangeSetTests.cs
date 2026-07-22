using THub.Domain.Publications;

namespace THub.Domain.Tests;

public sealed class PublicationChangeSetTests
{
    private static readonly DateTimeOffset SubmittedAt =
        new(2026, 7, 23, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ApprovedChangeSetTransitionsThroughApplyingToApplied()
    {
        var changeSet = CreateChangeSet();

        changeSet.Approve("DOMAIN\\approver", SubmittedAt.AddMinutes(1), "Reviewed");
        Assert.Equal(PublicationChangeSetStatus.Approved, changeSet.Status);

        changeSet.BeginApplying("thub-worker", SubmittedAt.AddMinutes(2));
        Assert.Equal(PublicationChangeSetStatus.Applying, changeSet.Status);

        changeSet.MarkApplied(SubmittedAt.AddMinutes(3));

        Assert.Equal(PublicationChangeSetStatus.Applied, changeSet.Status);
        Assert.Equal(SubmittedAt.AddMinutes(3), changeSet.CompletedAtUtc);
        Assert.Throws<InvalidOperationException>(() =>
            changeSet.MarkFailed("too late", SubmittedAt.AddMinutes(4)));
    }

    [Theory]
    [InlineData(PublicationChangeSetStatus.Conflict)]
    [InlineData(PublicationChangeSetStatus.Failed)]
    public void ApplyingChangeSetCanEndWithAuditedFailure(
        PublicationChangeSetStatus terminalStatus)
    {
        var changeSet = CreateChangeSet();
        changeSet.Approve("DOMAIN\\approver", SubmittedAt.AddMinutes(1));
        changeSet.BeginApplying("thub-worker", SubmittedAt.AddMinutes(2));

        if (terminalStatus == PublicationChangeSetStatus.Conflict)
        {
            changeSet.MarkConflict("Source row changed.", SubmittedAt.AddMinutes(3));
        }
        else
        {
            changeSet.MarkFailed("Transient source failure.", SubmittedAt.AddMinutes(3));
        }

        Assert.Equal(terminalStatus, changeSet.Status);
        Assert.NotNull(changeSet.OutcomeDetail);
    }

    [Fact]
    public void PendingChangeSetCanBeRejectedButCannotThenApply()
    {
        var changeSet = CreateChangeSet();

        changeSet.Reject("DOMAIN\\approver", "Invalid requested value.", SubmittedAt.AddMinutes(1));

        Assert.Equal(PublicationChangeSetStatus.Rejected, changeSet.Status);
        Assert.Equal(changeSet.ReviewedAtUtc, changeSet.CompletedAtUtc);
        Assert.Throws<InvalidOperationException>(() =>
            changeSet.BeginApplying("thub-worker", SubmittedAt.AddMinutes(2)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GrantPolicyChangeInvalidatesPendingOrApprovedChangeSet(bool approveFirst)
    {
        var changeSet = CreateChangeSet();
        if (approveFirst)
        {
            changeSet.Approve("DOMAIN\\approver", SubmittedAt.AddMinutes(1), "Original approval");
        }

        changeSet.InvalidateAuthorization(SubmittedAt.AddMinutes(2));

        Assert.Equal(PublicationChangeSetStatus.Rejected, changeSet.Status);
        Assert.Equal(SubmittedAt.AddMinutes(2), changeSet.CompletedAtUtc);
        Assert.Equal(PublicationChangeSet.AuthorizationChangedOutcome, changeSet.OutcomeDetail);
        Assert.Equal(approveFirst ? "Original approval" : null, changeSet.ReviewComment);
        Assert.Throws<InvalidOperationException>(() =>
            changeSet.BeginApplying("thub-worker", SubmittedAt.AddMinutes(3)));
    }

    [Fact]
    public void ChangePayloadShapeMatchesItsSeparateOperation()
    {
        var changeSetId = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() =>
            new PublicationChange(
                Guid.NewGuid(),
                changeSetId,
                PublicationChangeOperation.Insert,
                null,
                "{\"id\":1}",
                "{\"id\":1}"));
        Assert.Throws<ArgumentException>(() =>
            new PublicationChange(
                Guid.NewGuid(),
                changeSetId,
                PublicationChangeOperation.Update,
                "{\"id\":1}",
                null,
                "{\"id\":1}"));
        Assert.Throws<ArgumentException>(() =>
            new PublicationChange(
                Guid.NewGuid(),
                changeSetId,
                PublicationChangeOperation.Delete,
                "{\"id\":1}",
                "{\"id\":1}",
                "{\"id\":1}"));
    }

    [Fact]
    public void BeforeAndAfterJsonAreValidatedAndBounded()
    {
        var changeSetId = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() =>
            new PublicationChange(
                Guid.NewGuid(),
                changeSetId,
                PublicationChangeOperation.Insert,
                null,
                null,
                "not-json"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PublicationChange(
                Guid.NewGuid(),
                changeSetId,
                PublicationChangeOperation.Insert,
                null,
                null,
                "{\"value\":\"" + new string('x', PublicationChange.MaximumRowJsonLength) + "\"}"));
    }

    private static PublicationChangeSet CreateChangeSet()
    {
        var changeSetId = Guid.NewGuid();
        var change = new PublicationChange(
            Guid.NewGuid(),
            changeSetId,
            PublicationChangeOperation.Update,
            "{\"id\":1}",
            "{\"id\":1,\"name\":\"before\"}",
            "{\"id\":1,\"name\":\"after\"}");

        return new PublicationChangeSet(
            changeSetId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            [change],
            "DOMAIN\\editor",
            SubmittedAt);
    }
}
