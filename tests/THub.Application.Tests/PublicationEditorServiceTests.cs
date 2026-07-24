using THub.Application.Publications;
using THub.Domain.Publications;

namespace THub.Application.Tests;

public sealed class PublicationEditorServiceTests
{
    [Fact]
    public async Task StageAsync_RejectsEmptyChangeSetBeforeLoadingPublication()
    {
        var service = CreateService(
            new FakePublicationCatalogStore(),
            new FakePublicationGrantStore(),
            new FakePublicationChangeSetStore());

        var result = await service.StageAsync(
            new StagePublicationChangeSetCommand(
                Guid.NewGuid(),
                [PublicationRole.Administrator],
                [],
                "CONTOSO\\editor"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(PublicationProblemKind.Validation, result.Problem!.Kind);
        Assert.Equal("publication.change_command_invalid", result.Problem.Code);
    }

    [Fact]
    public async Task StageAsync_ReturnsForbiddenWhenRoleCannotUpdate()
    {
        var (publication, version) = PublicationTestData.CreateActiveEditorPublication();
        var catalog = CreateCatalog(publication, version);
        var grants = new FakePublicationGrantStore();
        grants.Grants.Add(new PublicationGrant(
            Guid.NewGuid(), publication.Id, PublicationRole.Viewer, true, false, false, false, false));
        var service = CreateService(catalog, grants, new FakePublicationChangeSetStore());

        var result = await service.StageAsync(
            CreateUpdateCommand(publication.Id, [PublicationRole.Viewer]),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(PublicationProblemKind.Forbidden, result.Problem!.Kind);
    }

    [Fact]
    public async Task StageAndReviewAsync_PersistsPendingThenApprovedChangeSet()
    {
        var (publication, version) = PublicationTestData.CreateActiveEditorPublication();
        var catalog = CreateCatalog(publication, version);
        var grants = new FakePublicationGrantStore();
        grants.Grants.Add(new PublicationGrant(
            Guid.NewGuid(), publication.Id, PublicationRole.Operator, true, false, true, false, true));
        var changes = new FakePublicationChangeSetStore();
        var service = CreateService(catalog, grants, changes);

        var staged = await service.StageAsync(
            CreateUpdateCommand(publication.Id, [PublicationRole.Operator]),
            CancellationToken.None);
        var reviewed = await service.ReviewAsync(
            new ReviewPublicationChangeSetCommand(
                publication.Id,
                staged.Value!.Id,
                [PublicationRole.Operator],
                PublicationChangeReviewDecision.Approve,
                "Approved",
                "CONTOSO\\reviewer"),
            CancellationToken.None);

        Assert.True(staged.IsSuccess);
        Assert.True(reviewed.IsSuccess);
        Assert.Equal(PublicationChangeSetStatus.Approved, reviewed.Value!.Status);
        Assert.Equal("CONTOSO\\reviewer", reviewed.Value.ReviewedBy);
        var fingerprint = PublicationGrantFingerprint.Compute(grants.Grants);
        Assert.Equal(fingerprint, changes.LastAddGrantFingerprint);
        Assert.Equal(fingerprint, changes.LastUpdateGrantFingerprint);
    }

    [Fact]
    public async Task ReviewAsync_ReturnsConflictWhenAtomicGrantRecheckFails()
    {
        var (publication, version) = PublicationTestData.CreateActiveEditorPublication();
        var catalog = CreateCatalog(publication, version);
        var grants = new FakePublicationGrantStore();
        grants.Grants.Add(new PublicationGrant(
            Guid.NewGuid(), publication.Id, PublicationRole.Operator, true, false, true, false, true));
        var changes = new FakePublicationChangeSetStore
        {
            UpdateStatus = PublicationChangeSetWriteStatus.Conflict,
        };
        var service = CreateService(catalog, grants, changes);
        var staged = await service.StageAsync(
            CreateUpdateCommand(publication.Id, [PublicationRole.Operator]),
            CancellationToken.None);

        var reviewed = await service.ReviewAsync(
            new ReviewPublicationChangeSetCommand(
                publication.Id,
                staged.Value!.Id,
                [PublicationRole.Operator],
                PublicationChangeReviewDecision.Approve,
                null,
                "CONTOSO\\reviewer"),
            CancellationToken.None);

        Assert.False(reviewed.IsSuccess);
        Assert.Equal("publication.change_review_conflict", reviewed.Problem!.Code);
        Assert.Equal(
            PublicationGrantFingerprint.Compute(grants.Grants),
            changes.LastUpdateGrantFingerprint);
    }

    [Fact]
    public async Task StageAsync_RejectsUnknownOrNonWritableAliases()
    {
        var (publication, version) = PublicationTestData.CreateActiveEditorPublication();
        var catalog = CreateCatalog(publication, version);
        var grants = new FakePublicationGrantStore();
        grants.Grants.Add(new PublicationGrant(
            Guid.NewGuid(), publication.Id, PublicationRole.Operator, true, false, true, false, false));
        var changes = new FakePublicationChangeSetStore();
        var service = CreateService(catalog, grants, changes);
        var command = new StagePublicationChangeSetCommand(
            publication.Id,
            [PublicationRole.Operator],
            [new StagePublicationChangeCommand(
                PublicationChangeOperation.Update,
                "{\"id\":1}",
                "{\"id\":1,\"name\":\"Old\"}",
                "{\"notExposed\":\"New\"}")],
            "CONTOSO\\editor");

        var result = await service.StageAsync(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(PublicationProblemKind.Validation, result.Problem!.Kind);
        Assert.Empty(changes.ChangeSets);
    }

    [Fact]
    public async Task StageAsync_AllowsNaturalKeyOnInsert()
    {
        var (publication, version) = PublicationTestData.CreateActiveEditorPublication();
        var catalog = CreateCatalog(publication, version);
        var grants = new FakePublicationGrantStore();
        grants.Grants.Add(new PublicationGrant(
            Guid.NewGuid(), publication.Id, PublicationRole.Operator, true, true, false, false, false));
        var changes = new FakePublicationChangeSetStore();
        var service = CreateService(catalog, grants, changes);
        var command = new StagePublicationChangeSetCommand(
            publication.Id,
            [PublicationRole.Operator],
            [new StagePublicationChangeCommand(
                PublicationChangeOperation.Insert,
                null,
                null,
                "{\"id\":2,\"name\":\"New\"}")],
            "CONTOSO\\editor");

        var result = await service.StageAsync(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(changes.ChangeSets);
    }

    [Fact]
    public async Task StageAsync_RejectsNaturalKeyMutationOnUpdate()
    {
        var (publication, version) = PublicationTestData.CreateActiveEditorPublication();
        var catalog = CreateCatalog(publication, version);
        var grants = new FakePublicationGrantStore();
        grants.Grants.Add(new PublicationGrant(
            Guid.NewGuid(), publication.Id, PublicationRole.Operator, true, false, true, false, false));
        var changes = new FakePublicationChangeSetStore();
        var service = CreateService(catalog, grants, changes);
        var command = new StagePublicationChangeSetCommand(
            publication.Id,
            [PublicationRole.Operator],
            [new StagePublicationChangeCommand(
                PublicationChangeOperation.Update,
                "{\"id\":1}",
                "{\"id\":1,\"name\":\"Old\"}",
                "{\"id\":2}")],
            "CONTOSO\\editor");

        var result = await service.StageAsync(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("publication.change_after_invalid", result.Problem!.Code);
        Assert.Empty(changes.ChangeSets);
    }

    [Fact]
    public async Task StageAsync_ValidatesApprovedForeignKeyAgainstSourceBeforePersisting()
    {
        var (publication, version) = PublicationTestData.CreateActiveEditorPublication(withForeignKey: true);
        var catalog = CreateCatalog(publication, version);
        var grants = new FakePublicationGrantStore();
        grants.Grants.Add(new PublicationGrant(
            Guid.NewGuid(), publication.Id, PublicationRole.Operator, true, true, false, false, false));
        var changes = new FakePublicationChangeSetStore();
        var source = new FakePublicationSourceDataReader
        {
            ResolutionResult = new PublicationSourceReadResult<PublicationSourceForeignKeyResolution>(
                PublicationSourceReadStatus.Success,
                new PublicationSourceForeignKeyResolution(
                    [new PublicationForeignKeyLabelDto(0, "Operations")])),
        };
        var service = CreateService(catalog, grants, changes, source);

        var result = await service.StageAsync(
            new StagePublicationChangeSetCommand(
                publication.Id,
                [PublicationRole.Operator],
                [new StagePublicationChangeCommand(
                    PublicationChangeOperation.Insert,
                    null,
                    null,
                    "{\"id\":2,\"name\":\"New\",\"departmentId\":7}")],
                "CONTOSO\\editor"),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Problem?.Message);
        var tuple = Assert.Single(source.LastResolutionTuples!);
        Assert.Equal("FK_Order_Department", tuple.ConstraintName);
        Assert.Equal("7", tuple.KeyValues["departmentId"]);
        Assert.Single(changes.ChangeSets);
    }

    [Fact]
    public async Task StageAsync_RejectsForeignKeyTupleThatDoesNotExist()
    {
        var (publication, version) = PublicationTestData.CreateActiveEditorPublication(withForeignKey: true);
        var catalog = CreateCatalog(publication, version);
        var grants = new FakePublicationGrantStore();
        grants.Grants.Add(new PublicationGrant(
            Guid.NewGuid(), publication.Id, PublicationRole.Operator, true, true, false, false, false));
        var changes = new FakePublicationChangeSetStore();
        var service = CreateService(catalog, grants, changes, new FakePublicationSourceDataReader());

        var result = await service.StageAsync(
            new StagePublicationChangeSetCommand(
                publication.Id,
                [PublicationRole.Operator],
                [new StagePublicationChangeCommand(
                    PublicationChangeOperation.Insert,
                    null,
                    null,
                    "{\"id\":2,\"name\":\"New\",\"departmentId\":999}")],
                "CONTOSO\\editor"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("publication.foreign_key_not_found", result.Problem!.Code);
        Assert.Empty(changes.ChangeSets);
    }

    [Fact]
    public async Task StageAsync_RejectsPartialCompositeForeignKeyUpdate()
    {
        var (publication, version) = CreateCompositeForeignKeyPublication();
        var catalog = CreateCatalog(publication, version);
        var grants = new FakePublicationGrantStore();
        grants.Grants.Add(new PublicationGrant(
            Guid.NewGuid(), publication.Id, PublicationRole.Operator, true, false, true, false, false));
        var changes = new FakePublicationChangeSetStore();
        var service = CreateService(catalog, grants, changes);

        var result = await service.StageAsync(
            new StagePublicationChangeSetCommand(
                publication.Id,
                [PublicationRole.Operator],
                [new StagePublicationChangeCommand(
                    PublicationChangeOperation.Update,
                    "{\"id\":1}",
                    "{\"id\":1,\"tenantId\":10,\"customerId\":20}",
                    "{\"tenantId\":11}")],
                "CONTOSO\\editor"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("publication.foreign_key_partial_update", result.Problem!.Code);
        Assert.Empty(changes.ChangeSets);
    }

    private static StagePublicationChangeSetCommand CreateUpdateCommand(
        Guid publicationId,
        IReadOnlyCollection<Guid> roles) =>
        new(
            publicationId,
            roles,
            [new StagePublicationChangeCommand(
                PublicationChangeOperation.Update,
                "{\"id\":1}",
                "{\"id\":1,\"name\":\"Old\"}",
                "{\"name\":\"New\"}")],
            "CONTOSO\\editor");

    private static FakePublicationCatalogStore CreateCatalog(
        Publication publication,
        PublicationVersion version)
    {
        var catalog = new FakePublicationCatalogStore();
        catalog.Publications.Add(publication);
        catalog.Versions.Add(version);
        return catalog;
    }

    private static (Publication Publication, PublicationVersion Version) CreateCompositeForeignKeyPublication()
    {
        var publication = new Publication(
            Guid.NewGuid(),
            "customer-editor",
            "Customer editor",
            PublicationKind.Editor,
            "CONTOSO\\author",
            PublicationTestData.Now);
        var versionId = Guid.NewGuid();
        PublicationForeignKey ForeignKey(int ordinal, string referencedColumn) => new(
            "FK_Order_Customer",
            ordinal,
            2,
            "dbo",
            "Customers",
            referencedColumn,
            "DisplayName",
            ["DisplayName"],
            PublicationLookupMode.ServerFiltered);
        var columns = new[]
        {
            new PublicationColumn(
                Guid.NewGuid(), versionId, 0, "Id", "id", PublicationDataType.Int32, "int",
                false, true, true, true, false, true, 0),
            new PublicationColumn(
                Guid.NewGuid(), versionId, 1, "TenantId", "tenantId", PublicationDataType.Int32, "int",
                false, true, true, true, true, foreignKey: ForeignKey(0, "TenantId")),
            new PublicationColumn(
                Guid.NewGuid(), versionId, 2, "CustomerId", "customerId", PublicationDataType.Int32, "int",
                false, true, true, true, true, foreignKey: ForeignKey(1, "CustomerId")),
        };
        var version = new PublicationVersion(
            versionId,
            publication.Id,
            1,
            Guid.NewGuid(),
            "dbo",
            "Orders",
            PublicationSourceObjectKind.Table,
            "schema-v1",
            PublicationConcurrencyMode.OriginalValues,
            new PublicationVersionSettings(),
            columns,
            "CONTOSO\\author",
            PublicationTestData.Now,
            Guid.NewGuid());
        publication.ActivateVersion(version, "CONTOSO\\author", PublicationTestData.Now);
        return (publication, version);
    }

    private static PublicationEditorService CreateService(
        FakePublicationCatalogStore catalog,
        FakePublicationGrantStore grants,
        FakePublicationChangeSetStore changes,
        FakePublicationSourceDataReader? source = null) =>
        new(
            catalog,
            changes,
            source ?? new FakePublicationSourceDataReader(),
            new PublicationAuthorizationService(catalog, grants),
            new FixedTimeProvider(PublicationTestData.Now));
}
