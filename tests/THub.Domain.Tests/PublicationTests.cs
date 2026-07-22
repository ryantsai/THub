using THub.Domain.Publications;

namespace THub.Domain.Tests;

public sealed class PublicationTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 7, 23, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NewPublicationNormalizesUniqueReadySlugAndAuditValues()
    {
        var publication = new Publication(
            Guid.Parse("65922745-a9f6-42bd-9ac9-81c8149c8179"),
            "  Customer__Order Results  ",
            "  Customer orders  ",
            PublicationKind.RestApi,
            "  DOMAIN\\designer  ",
            CreatedAt);

        Assert.Equal("customer-order-results", publication.Slug);
        Assert.Equal("Customer orders", publication.Name);
        Assert.Equal(PublicationState.Draft, publication.State);
        Assert.Null(publication.ActiveVersionId);
        Assert.Equal(TimeSpan.Zero, publication.CreatedAtUtc.Offset);
        Assert.Equal(publication.CreatedAtUtc, publication.UpdatedAtUtc);
        Assert.Equal("DOMAIN\\designer", publication.UpdatedBy);
    }

    [Fact]
    public void PublicationActivatesOnlyItsOwnVersionAndPreservesStableIdentity()
    {
        var publication = CreatePublication();
        var version = PublicationVersionTests.CreateVersion(publication.Id);
        var activatedAt = CreatedAt.AddMinutes(5);

        publication.ActivateVersion(version, "DOMAIN\\reviewer", activatedAt);

        Assert.Equal(PublicationState.Active, publication.State);
        Assert.Equal(version.Id, publication.ActiveVersionId);
        Assert.Equal("published-table", publication.Slug);
        Assert.Equal("DOMAIN\\reviewer", publication.UpdatedBy);
        Assert.Throws<InvalidOperationException>(() =>
            publication.ChangeSlug("different-route", "DOMAIN\\reviewer", activatedAt.AddMinutes(1)));

        publication.Disable("DOMAIN\\reviewer", activatedAt.AddMinutes(2));
        publication.ChangeSlug("Different Route", "DOMAIN\\reviewer", activatedAt.AddMinutes(3));

        Assert.Equal(publication.Id, version.PublicationId);
        Assert.Equal("different-route", publication.Slug);
        Assert.Equal(PublicationState.Disabled, publication.State);
    }

    [Fact]
    public void PublicationRejectsVersionOwnedByAnotherPublication()
    {
        var publication = CreatePublication();
        var foreignVersion = PublicationVersionTests.CreateVersion(Guid.NewGuid());

        Assert.Throws<ArgumentException>(() =>
            publication.ActivateVersion(foreignVersion, "DOMAIN\\reviewer", CreatedAt.AddMinutes(5)));
    }

    [Theory]
    [InlineData("---")]
    [InlineData("contains/slash")]
    [InlineData("contains.dot")]
    public void SlugRejectsValuesThatCannotFormSafeRoutes(string slug)
    {
        Assert.Throws<ArgumentException>(() => Publication.NormalizeSlug(slug));
    }

    private static Publication CreatePublication() =>
        new(
            "Published Table",
            "Published table",
            PublicationKind.Editor,
            "DOMAIN\\designer",
            CreatedAt);
}
