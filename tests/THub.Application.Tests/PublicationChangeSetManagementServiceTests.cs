using THub.Application.Publications;
using THub.Domain.Publications;

namespace THub.Application.Tests;

public sealed class PublicationChangeSetManagementServiceTests
{
    [Fact]
    public async Task ListAndGetAsync_ReturnBoundedAuthorizedChangeSets()
    {
        var (publication, version) = PublicationTestData.CreateActiveEditorPublication();
        var catalog = new FakePublicationCatalogStore();
        catalog.Publications.Add(publication);
        catalog.Versions.Add(version);
        var grants = new FakePublicationGrantStore();
        grants.Grants.Add(new PublicationGrant(
            Guid.NewGuid(), publication.Id, PublicationRole.Viewer, true, false, false, false, false));
        var changeSetId = Guid.NewGuid();
        var changeSet = new PublicationChangeSet(
            changeSetId,
            publication.Id,
            version.Id,
            [new PublicationChange(
                Guid.NewGuid(),
                changeSetId,
                PublicationChangeOperation.Update,
                "{\"id\":1}",
                "{\"id\":1,\"name\":\"Old\"}",
                "{\"name\":\"New\"}")],
            "CONTOSO\\editor",
            PublicationTestData.Now);
        var store = new QueryStore(changeSet);
        var service = new PublicationChangeSetManagementService(
            store,
            new PublicationAuthorizationService(catalog, grants));

        var page = await service.ListAsync(
            new PublicationChangeSetListQuery(
                publication.Id,
                [PublicationRole.Viewer],
                Take: 20),
            CancellationToken.None);
        var detail = await service.GetAsync(
            publication.Id,
            changeSet.Id,
            [PublicationRole.Viewer],
            CancellationToken.None);

        Assert.True(page.IsSuccess);
        Assert.Single(page.Value!.Items);
        Assert.True(detail.IsSuccess);
        Assert.Single(detail.Value!.Changes);
    }

    [Fact]
    public async Task ListAsync_RequiresViewGrant()
    {
        var (publication, _) = PublicationTestData.CreateActiveEditorPublication();
        var catalog = new FakePublicationCatalogStore();
        catalog.Publications.Add(publication);
        var service = new PublicationChangeSetManagementService(
            new QueryStore(),
            new PublicationAuthorizationService(catalog, new FakePublicationGrantStore()));

        var result = await service.ListAsync(
            new PublicationChangeSetListQuery(publication.Id, [PublicationRole.Viewer]),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(PublicationProblemKind.Forbidden, result.Problem!.Kind);
    }

    private sealed class QueryStore(params PublicationChangeSet[] changeSets) : IPublicationChangeSetQueryStore
    {
        public Task<PublicationChangeSetQueryPage> ListAsync(
            Guid publicationId,
            IReadOnlyCollection<PublicationChangeSetStatus> statuses,
            int take,
            DateTimeOffset? beforeSubmittedAtUtc,
            Guid? beforeId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PublicationChangeSetQueryPage(
                changeSets.Where(changeSet => changeSet.PublicationId == publicationId).Take(take).ToArray(),
                false));

        public Task<PublicationChangeSet?> FindDetailAsync(
            Guid publicationId,
            Guid changeSetId,
            CancellationToken cancellationToken) =>
            Task.FromResult(changeSets.SingleOrDefault(changeSet =>
                changeSet.PublicationId == publicationId && changeSet.Id == changeSetId));
    }
}
