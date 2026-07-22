using THub.Domain.Publications;

namespace THub.Domain.Tests;

public sealed class PublicationAccessTokenTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 7, 23, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AcceptedRequestsAreMeteredOnlyWhileTokenIsActive()
    {
        var token = CreateToken();
        var acceptedAt = CreatedAt.AddMinutes(1);

        token.RecordAcceptedRequest(acceptedAt);
        token.RecordAcceptedRequest(acceptedAt.AddSeconds(1));

        Assert.Equal(2, token.AcceptedRequestCount);
        Assert.Equal(acceptedAt.AddSeconds(1), token.LastUsedAtUtc);
        Assert.Equal(PublicationAccessTokenStatus.Active, token.GetStatus(acceptedAt));
        Assert.Throws<InvalidOperationException>(() =>
            token.RecordAcceptedRequest(token.ExpiresAtUtc));
    }

    [Fact]
    public void ExpiryIsInclusiveAndRevocationIsIrreversible()
    {
        var token = CreateToken();

        Assert.Equal(
            PublicationAccessTokenStatus.Expired,
            token.GetStatus(token.ExpiresAtUtc));

        token.Revoke("DOMAIN\\administrator", CreatedAt.AddMinutes(2));

        Assert.Equal(
            PublicationAccessTokenStatus.Revoked,
            token.GetStatus(CreatedAt.AddMinutes(2)));
        Assert.False(token.IsUsableAt(CreatedAt.AddMinutes(2)));
        Assert.Throws<InvalidOperationException>(() =>
            token.Revoke("DOMAIN\\administrator", CreatedAt.AddMinutes(3)));
        Assert.Throws<InvalidOperationException>(() =>
            token.RecordAcceptedRequest(CreatedAt.AddMinutes(3)));
    }

    [Fact]
    public void ConstructorStoresOnlyManagedVerifierMetadata()
    {
        var token = CreateToken();

        Assert.Equal("selector_12345678", token.Selector);
        Assert.Equal(new string('a', 64), token.Verifier);
        Assert.Equal(1, token.AlgorithmVersion);
        Assert.Equal("thub_abcd", token.DisplayPrefix);
        Assert.DoesNotContain(
            typeof(PublicationAccessToken).GetProperties(),
            property => property.Name.Contains("Plaintext", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Equals("Secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExpiryMustFollowCreation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateToken(CreatedAt));
    }

    private static PublicationAccessToken CreateToken(DateTimeOffset? expiresAtUtc = null) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Integration A",
            "selector_12345678",
            new string('a', 64),
            algorithmVersion: 1,
            "thub_abcd",
            "DOMAIN\\administrator",
            CreatedAt,
            expiresAtUtc ?? CreatedAt.AddHours(1));
}
