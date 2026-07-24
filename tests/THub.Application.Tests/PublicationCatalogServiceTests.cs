using THub.Application.Publications;
using THub.Domain.Publications;

namespace THub.Application.Tests;

public sealed class PublicationCatalogServiceTests
{
    [Fact]
    public async Task CreateAsync_ReturnsConflictForDuplicateSlug()
    {
        var store = new FakePublicationCatalogStore
        {
            AddPublicationStatus = PublicationCatalogWriteStatus.DuplicateSlug,
        };
        var service = new PublicationCatalogService(
            store,
            new FakePublicationConnectionPolicy(),
            new FixedTimeProvider(PublicationTestData.Now));

        var result = await service.CreateAsync(
            new CreatePublicationCommand("Orders API", "Orders", PublicationKind.RestApi, "CONTOSO\\author"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(PublicationProblemKind.Conflict, result.Problem!.Kind);
        Assert.Equal("publication.slug_exists", result.Problem.Code);
    }

    [Fact]
    public async Task CreateVersionAsync_RejectsWritableRestVersion()
    {
        var publication = new Publication(
            Guid.NewGuid(),
            "orders-api",
            "Orders",
            PublicationKind.RestApi,
            "CONTOSO\\author",
            PublicationTestData.Now);
        var store = new FakePublicationCatalogStore();
        store.Publications.Add(publication);
        var service = new PublicationCatalogService(
            store,
            new FakePublicationConnectionPolicy(),
            new FixedTimeProvider(PublicationTestData.Now));

        var result = await service.CreateVersionAsync(
            new CreatePublicationVersionCommand(
                publication.Id,
                Guid.NewGuid(),
                Guid.NewGuid(),
                "dbo",
                "Orders",
                PublicationSourceObjectKind.Table,
                "schema-v1",
                PublicationConcurrencyMode.OriginalValues,
                new PublicationVersionSettings(),
                [
                    new CreatePublicationColumnCommand(
                        0,
                        "OrderId",
                        "id",
                        PublicationDataType.Int32,
                        "int",
                        false,
                        true,
                        true,
                        true,
                        false,
                        true,
                        0),
                    new CreatePublicationColumnCommand(
                        1,
                        "Name",
                        "name",
                        PublicationDataType.String,
                        "nvarchar",
                        false,
                        true,
                        true,
                        true,
                        true),
                ],
                "CONTOSO\\author"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(PublicationProblemKind.Validation, result.Problem!.Kind);
        Assert.Equal("publication.rest_read_only", result.Problem.Code);
        Assert.Empty(store.Versions);
    }

    [Fact]
    public async Task ActivateAsync_FreezesTheSelectedActiveVersionReference()
    {
        var publication = new Publication(
            Guid.NewGuid(),
            "orders-editor",
            "Orders",
            PublicationKind.Editor,
            "CONTOSO\\author",
            PublicationTestData.Now);
        var version = PublicationTestData.CreateVersion(publication.Id, writable: true, withForeignKey: false);
        var store = new FakePublicationCatalogStore();
        store.Publications.Add(publication);
        store.Versions.Add(version);
        var service = new PublicationCatalogService(
            store,
            new FakePublicationConnectionPolicy(),
            new FixedTimeProvider(PublicationTestData.Now.AddMinutes(1)));

        var result = await service.ActivateAsync(
            publication.Id,
            version.Id,
            "CONTOSO\\publisher",
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(PublicationState.Active, result.Value!.State);
        Assert.Equal(version.Id, result.Value.ActiveVersionId);
    }

    [Fact]
    public async Task ActivateAsync_RejectsVersionWhenApplyConnectionPolicyFails()
    {
        var publication = new Publication(
            Guid.NewGuid(),
            "orders-editor",
            "Orders",
            PublicationKind.Editor,
            "CONTOSO\\author",
            PublicationTestData.Now);
        var version = PublicationTestData.CreateVersion(
            publication.Id,
            writable: true,
            withForeignKey: false);
        var store = new FakePublicationCatalogStore();
        store.Publications.Add(publication);
        store.Versions.Add(version);
        var policy = new FakePublicationConnectionPolicy
        {
            Result = PublicationConnectionPolicyResult.Failure(
                "publication.connection_target_mismatch",
                "Read and apply connections must target the same database endpoint."),
        };
        var service = new PublicationCatalogService(
            store,
            policy,
            new FixedTimeProvider(PublicationTestData.Now.AddMinutes(1)));

        var result = await service.ActivateAsync(
            publication.Id,
            version.Id,
            "CONTOSO\\publisher",
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "publication.connection_target_mismatch",
            result.Problem!.Code);
        Assert.Null(publication.ActiveVersionId);
    }
}
