using THub.Application.Publications;
using THub.Domain.Publications;

namespace THub.Application.Tests;

public sealed class PublicationGrantManagementServiceTests
{
    [Fact]
    public async Task ReplaceAsync_PersistsSeparateEditorPermissions()
    {
        var (publication, _) = PublicationTestData.CreateActiveEditorPublication();
        var catalog = new FakePublicationCatalogStore();
        catalog.Publications.Add(publication);
        var reads = new GrantStore();
        var service = new PublicationGrantManagementService(catalog, reads, reads);
        var current = await service.GetAsync(publication.Id, CancellationToken.None);

        var result = await service.ReplaceAsync(
            new ReplacePublicationGrantsCommand(
                publication.Id,
                current.Value!.Fingerprint,
                [
                    new PublicationGrantCommand(
                        PublicationRole.Viewer,
                        true,
                        false,
                        false,
                        false,
                        false),
                    new PublicationGrantCommand(
                        PublicationRole.Operator,
                        true,
                        true,
                        true,
                        false,
                        false),
                ]),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Collection(
            result.Value!.Grants,
            viewer =>
            {
                Assert.Equal(PublicationRole.Viewer, viewer.RoleId);
                Assert.True(viewer.CanView);
                Assert.False(viewer.CanUpdate);
            },
            editor =>
            {
                Assert.Equal(PublicationRole.Operator, editor.RoleId);
                Assert.True(editor.CanInsert);
                Assert.True(editor.CanUpdate);
                Assert.False(editor.CanDelete);
            });
    }

    [Fact]
    public async Task ReplaceAsync_RejectsRestPublicationGrants()
    {
        var (publication, _) = PublicationTestData.CreateActiveRestPublication();
        var catalog = new FakePublicationCatalogStore();
        catalog.Publications.Add(publication);
        var grants = new GrantStore();
        var service = new PublicationGrantManagementService(catalog, grants, grants);

        var result = await service.ReplaceAsync(
            new ReplacePublicationGrantsCommand(
                publication.Id,
                PublicationGrantFingerprint.Compute([]),
                [new PublicationGrantCommand(
                    PublicationRole.Operator, true, true, true, true, true)]),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(PublicationProblemKind.Conflict, result.Problem!.Kind);
        Assert.Empty(grants.Grants);
    }

    [Fact]
    public async Task ReplaceAsync_ReturnsConflictForStaleFingerprint()
    {
        var (publication, _) = PublicationTestData.CreateActiveEditorPublication();
        var catalog = new FakePublicationCatalogStore();
        catalog.Publications.Add(publication);
        var grants = new GrantStore { WriteStatus = PublicationGrantWriteStatus.Conflict };
        var service = new PublicationGrantManagementService(catalog, grants, grants);

        var result = await service.ReplaceAsync(
            new ReplacePublicationGrantsCommand(publication.Id, new string('A', 64), []),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(PublicationProblemKind.Conflict, result.Problem!.Kind);
    }

    private sealed class GrantStore : IPublicationGrantStore, IPublicationGrantManagementStore
    {
        public List<PublicationGrant> Grants { get; } = [];

        public PublicationGrantWriteStatus WriteStatus { get; set; } = PublicationGrantWriteStatus.Saved;

        public Task<IReadOnlyList<PublicationGrant>> ListAsync(
            Guid publicationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<PublicationGrant>>(
                Grants.Where(grant => grant.PublicationId == publicationId).ToArray());
        }

        public Task<PublicationGrantWriteStatus> ReplaceAsync(
            Guid publicationId,
            string expectedFingerprint,
            IReadOnlyList<PublicationGrant> grants,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (WriteStatus == PublicationGrantWriteStatus.Saved)
            {
                Grants.Clear();
                Grants.AddRange(grants);
            }

            return Task.FromResult(WriteStatus);
        }
    }
}
