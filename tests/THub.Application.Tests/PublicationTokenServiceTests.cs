using THub.Application.Publications;

namespace THub.Application.Tests;

public sealed class PublicationTokenServiceTests
{
    [Fact]
    public async Task CreateAndListAsync_SupportsMultipleTokensWithoutListingPlaintext()
    {
        var (publication, version) = PublicationTestData.CreateActiveRestPublication();
        var catalog = new FakePublicationCatalogStore();
        catalog.Publications.Add(publication);
        catalog.Versions.Add(version);
        var tokens = new FakePublicationTokenStore();
        var service = CreateService(catalog, tokens);

        var first = await service.CreateAsync(
            new CreatePublicationTokenCommand(
                publication.Id,
                "ERP",
                PublicationTestData.Now.AddDays(30),
                "CONTOSO\\admin"),
            CancellationToken.None);
        var second = await service.CreateAsync(
            new CreatePublicationTokenCommand(
                publication.Id,
                "Reporting",
                PublicationTestData.Now.AddDays(30),
                "CONTOSO\\admin"),
            CancellationToken.None);
        var listed = await service.ListAsync(publication.Id, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.NotEqual(first.Value!.PlaintextToken, second.Value!.PlaintextToken);
        Assert.Equal(2, listed.Value!.Count);
        Assert.All(listed.Value, token => Assert.Equal(0, token.AcceptedRequestCount));
    }

    [Fact]
    public async Task AuthenticateAsync_RecordsAcceptedUseBeforeReturningSuccess()
    {
        var (publication, version) = PublicationTestData.CreateActiveRestPublication();
        var catalog = new FakePublicationCatalogStore();
        catalog.Publications.Add(publication);
        catalog.Versions.Add(version);
        var tokens = new FakePublicationTokenStore();
        var service = CreateService(catalog, tokens);
        var created = await service.CreateAsync(
            new CreatePublicationTokenCommand(
                publication.Id,
                "ERP",
                PublicationTestData.Now.AddDays(1),
                "CONTOSO\\admin"),
            CancellationToken.None);

        var result = await service.AuthenticateAndRecordAcceptedUseAsync(
            publication.Slug,
            created.Value!.PlaintextToken,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(version.Id, result.Value!.PublicationVersionId);
        Assert.Equal(1, tokens.MeterCalls);
        Assert.Equal(1, tokens.Tokens.Single().AcceptedRequestCount);
    }

    [Fact]
    public async Task AuthenticateAsync_FailsClosedWhenAtomicMeteringIsUnavailable()
    {
        var (publication, version) = PublicationTestData.CreateActiveRestPublication();
        var catalog = new FakePublicationCatalogStore();
        catalog.Publications.Add(publication);
        catalog.Versions.Add(version);
        var tokens = new FakePublicationTokenStore();
        var service = CreateService(catalog, tokens);
        var created = await service.CreateAsync(
            new CreatePublicationTokenCommand(
                publication.Id,
                "ERP",
                PublicationTestData.Now.AddDays(1),
                "CONTOSO\\admin"),
            CancellationToken.None);
        tokens.MeterStatus = PublicationAcceptedUseStatus.MeteringUnavailable;

        var result = await service.AuthenticateAndRecordAcceptedUseAsync(
            publication.Slug,
            created.Value!.PlaintextToken,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(PublicationProblemKind.Unavailable, result.Problem!.Kind);
        Assert.Equal(0, tokens.Tokens.Single().AcceptedRequestCount);
    }

    [Fact]
    public async Task AuthenticateAsync_DoesNotMeterInvalidCredential()
    {
        var (publication, version) = PublicationTestData.CreateActiveRestPublication();
        var catalog = new FakePublicationCatalogStore();
        catalog.Publications.Add(publication);
        catalog.Versions.Add(version);
        var tokens = new FakePublicationTokenStore();
        var service = CreateService(catalog, tokens);

        var result = await service.AuthenticateAndRecordAcceptedUseAsync(
            publication.Slug,
            "thub_abcdefghijklmnop.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(PublicationProblemKind.Unauthorized, result.Problem!.Kind);
        Assert.Equal(0, tokens.MeterCalls);
    }

    [Fact]
    public async Task AuthenticateAsync_DoesNotMeterTokenPresentedForAnotherRoute()
    {
        var (publication, version) = PublicationTestData.CreateActiveRestPublication();
        var catalog = new FakePublicationCatalogStore();
        catalog.Publications.Add(publication);
        catalog.Versions.Add(version);
        var tokens = new FakePublicationTokenStore();
        var service = CreateService(catalog, tokens);
        var created = await service.CreateAsync(
            new CreatePublicationTokenCommand(
                publication.Id,
                "ERP",
                PublicationTestData.Now.AddDays(1),
                "CONTOSO\\admin"),
            CancellationToken.None);

        var result = await service.AuthenticateAndRecordAcceptedUseAsync(
            "another-api",
            created.Value!.PlaintextToken,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(PublicationProblemKind.Unauthorized, result.Problem!.Kind);
        Assert.Equal(0, tokens.MeterCalls);
    }

    private static PublicationTokenService CreateService(
        FakePublicationCatalogStore catalog,
        FakePublicationTokenStore tokens) =>
        new(
            catalog,
            tokens,
            new PublicationTokenGenerator(),
            new FixedTimeProvider(PublicationTestData.Now));
}
