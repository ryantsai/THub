using THub.Application.Publications;
using THub.Domain.Publications;

namespace THub.Application.Tests;

public sealed class PublicationDataServiceTests
{
    [Fact]
    public async Task ReadRestRowsAsync_UsesBoundedApprovedMetadataAndAppendsKeySort()
    {
        var (publication, version) = PublicationTestData.CreateActiveRestPublication();
        var catalog = new FakePublicationCatalogStore();
        catalog.Publications.Add(publication);
        catalog.Versions.Add(version);
        var source = new FakePublicationSourceDataReader
        {
            RowResult = new PublicationSourceReadResult<PublicationSourceRowPage>(
                PublicationSourceReadStatus.Success,
                new PublicationSourceRowPage(
                    [new Dictionary<string, object?> { ["id"] = 1, ["name"] = "One" }],
                    "next")),
        };
        var service = CreateService(catalog, source, new FakePublicationGrantStore());

        var result = await service.ReadRestRowsAsync(
            new AuthenticatedPublicationTokenDto(
                Guid.NewGuid(),
                publication.Id,
                version.Id,
                PublicationTestData.Now),
            new PublicationRestRowsQuery(
                PageSize: 20,
                Filters: [new PublicationFilter("name", PublicationFilterOperator.StartsWith, "O")],
                Sorts: [new PublicationSort("name")]),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!.Rows);
        Assert.Equal(20, source.LastReadQuery!.Take);
        Assert.Collection(
            source.LastReadQuery.Sorts,
            sort => Assert.Equal("name", sort.ColumnAlias),
            sort => Assert.Equal("id", sort.ColumnAlias));
    }

    [Fact]
    public async Task ReadRestRowsAsync_RejectsUnapprovedFilterBeforeSourceRead()
    {
        var (publication, version) = PublicationTestData.CreateActiveRestPublication();
        var catalog = new FakePublicationCatalogStore();
        catalog.Publications.Add(publication);
        catalog.Versions.Add(version);
        var source = new FakePublicationSourceDataReader();
        var service = CreateService(catalog, source, new FakePublicationGrantStore());

        var result = await service.ReadRestRowsAsync(
            new AuthenticatedPublicationTokenDto(
                Guid.NewGuid(),
                publication.Id,
                version.Id,
                PublicationTestData.Now),
            new PublicationRestRowsQuery(
                Filters: [new PublicationFilter("secret", PublicationFilterOperator.Equal, "x")]),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(PublicationProblemKind.Validation, result.Problem!.Kind);
        Assert.Null(source.LastReadQuery);
    }

    [Fact]
    public async Task ReadRestRowsAsync_RejectsTypeInvalidFilterBeforeSourceRead()
    {
        var (publication, version) = PublicationTestData.CreateActiveRestPublication();
        var catalog = new FakePublicationCatalogStore();
        catalog.Publications.Add(publication);
        catalog.Versions.Add(version);
        var source = new FakePublicationSourceDataReader();
        var service = CreateService(catalog, source, new FakePublicationGrantStore());

        var result = await service.ReadRestRowsAsync(
            new AuthenticatedPublicationTokenDto(
                Guid.NewGuid(),
                publication.Id,
                version.Id,
                PublicationTestData.Now),
            new PublicationRestRowsQuery(
                Filters: [new PublicationFilter("id", PublicationFilterOperator.Contains, "1")]),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("publication.filter_invalid", result.Problem!.Code);
        Assert.Null(source.LastReadQuery);
    }

    [Fact]
    public async Task ReadRestRowsAsync_RejectsDuplicateSortBeforeSourceRead()
    {
        var (publication, version) = PublicationTestData.CreateActiveRestPublication();
        var catalog = new FakePublicationCatalogStore();
        catalog.Publications.Add(publication);
        catalog.Versions.Add(version);
        var source = new FakePublicationSourceDataReader();
        var service = CreateService(catalog, source, new FakePublicationGrantStore());

        var result = await service.ReadRestRowsAsync(
            new AuthenticatedPublicationTokenDto(
                Guid.NewGuid(),
                publication.Id,
                version.Id,
                PublicationTestData.Now),
            new PublicationRestRowsQuery(
                Sorts: [new PublicationSort("name"), new PublicationSort("NAME", true)]),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("publication.sort_invalid", result.Problem!.Code);
        Assert.Null(source.LastReadQuery);
    }

    [Fact]
    public async Task ReadForeignKeyLookupAsync_UsesOnlyConfiguredForeignKeyMetadata()
    {
        var (publication, version) = PublicationTestData.CreateActiveEditorPublication(withForeignKey: true);
        var catalog = new FakePublicationCatalogStore();
        catalog.Publications.Add(publication);
        catalog.Versions.Add(version);
        var grants = new FakePublicationGrantStore();
        grants.Grants.Add(new PublicationGrant(
            Guid.NewGuid(), publication.Id, PublicationRole.Viewer, true, false, false, false, false));
        var source = new FakePublicationSourceDataReader
        {
            LookupResult = new PublicationSourceReadResult<PublicationSourceLookupPage>(
                PublicationSourceReadStatus.Success,
                new PublicationSourceLookupPage(
                    [new PublicationLookupItemDto(
                        new Dictionary<string, object?> { ["DepartmentId"] = 7 },
                        "Operations")],
                    null)),
        };
        var service = CreateService(catalog, source, grants);

        var result = await service.ReadForeignKeyLookupAsync(
            new PublicationForeignKeyLookupQuery(
                publication.Id,
                "departmentId",
                [PublicationRole.Viewer],
                "oper",
                25),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!.Items);
        Assert.Equal("oper", source.LastLookupQuery!.Search);
        Assert.Equal(25, source.LastLookupQuery.Take);
    }

    [Fact]
    public async Task ResolveForeignKeyLabelsAsync_RequiresCompleteApprovedTupleAndViewGrant()
    {
        var (publication, version) = PublicationTestData.CreateActiveEditorPublication(withForeignKey: true);
        var catalog = new FakePublicationCatalogStore();
        catalog.Publications.Add(publication);
        catalog.Versions.Add(version);
        var grants = new FakePublicationGrantStore();
        grants.Grants.Add(new PublicationGrant(
            Guid.NewGuid(), publication.Id, PublicationRole.Viewer, true, false, false, false, false));
        var source = new FakePublicationSourceDataReader
        {
            ResolutionResult = new PublicationSourceReadResult<PublicationSourceForeignKeyResolution>(
                PublicationSourceReadStatus.Success,
                new PublicationSourceForeignKeyResolution(
                    [new PublicationForeignKeyLabelDto(17, "Operations")])),
        };
        var service = CreateService(catalog, source, grants);

        var result = await service.ResolveForeignKeyLabelsAsync(
            new PublicationForeignKeyLabelQuery(
                publication.Id,
                [PublicationRole.Viewer],
                [new PublicationForeignKeyLabelRequest(
                    17,
                    "departmentId",
                    new Dictionary<string, object?> { ["departmentId"] = 7 })]),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Problem?.Message);
        Assert.Equal("Operations", Assert.Single(result.Value!.Labels).DisplayText);
        Assert.Equal(7, Assert.Single(source.LastResolutionTuples!).KeyValues["departmentId"]);
    }

    private static PublicationDataService CreateService(
        FakePublicationCatalogStore catalog,
        FakePublicationSourceDataReader source,
        FakePublicationGrantStore grants) =>
        new(catalog, source, new PublicationAuthorizationService(catalog, grants));
}
