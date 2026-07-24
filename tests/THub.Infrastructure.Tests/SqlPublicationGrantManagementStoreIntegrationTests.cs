using Microsoft.EntityFrameworkCore;
using THub.Application.Publications;
using THub.Domain.Connections;
using THub.Domain.Publications;
using THub.Domain.Security;
using THub.Infrastructure.Persistence;
using THub.Infrastructure.Publications;

namespace THub.Infrastructure.Tests;

public sealed class SqlPublicationGrantManagementStoreIntegrationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GrantRevocationAtomicallyRejectsPendingAndApprovedSetsBeforeApply()
    {
        var fixture = CreateFixture();
        try
        {
            Publication publication;
            PublicationGrant currentGrant;
            await using (var setup = fixture.Factory.CreateDbContext())
            {
                await setup.Database.MigrateAsync();
                (publication, currentGrant) = await SeedEditorAsync(setup);
            }

            var replacement = new PublicationGrant(
                Guid.NewGuid(),
                publication.Id,
                PublicationRole.Operator,
                canView: true,
                canInsert: false,
                canUpdate: false,
                canDelete: false,
                canApprove: false);
            var store = new SqlPublicationGrantManagementStore(
                fixture.Factory,
                new FixedTimeProvider(Now.AddMinutes(10)));

            var status = await store.ReplaceAsync(
                publication.Id,
                PublicationGrantFingerprint.Compute([currentGrant]),
                [replacement],
                CancellationToken.None);

            Assert.Equal(PublicationGrantWriteStatus.Saved, status);
            var staleStage = CreateChangeSet(
                publication.Id,
                (await FindActiveVersionIdAsync(fixture.Factory, publication.Id)),
                "DOMAIN\\stale-editor",
                Now.AddMinutes(11));
            var staleStageStatus = await new SqlPublicationChangeSetStore(fixture.Factory).AddAsync(
                staleStage,
                PublicationGrantFingerprint.Compute([currentGrant]),
                CancellationToken.None);
            Assert.Equal(PublicationChangeSetWriteStatus.Conflict, staleStageStatus);

            await using var verification = fixture.Factory.CreateDbContext();
            var changeSets = await verification.PublicationChangeSets
                .AsNoTracking()
                .OrderBy(changeSet => changeSet.SubmittedAtUtc)
                .ToListAsync();
            Assert.Equal(2, changeSets.Count);
            Assert.All(changeSets, changeSet =>
            {
                Assert.Equal(PublicationChangeSetStatus.Rejected, changeSet.Status);
                Assert.Equal(Now.AddMinutes(10), changeSet.CompletedAtUtc);
                Assert.Equal(Now.AddMinutes(10), changeSet.UpdatedAtUtc);
                Assert.Equal(
                    PublicationChangeSet.AuthorizationChangedOutcome,
                    changeSet.OutcomeDetail);
            });
            Assert.Equal("DOMAIN\\approver", changeSets[1].ReviewedBy);
            Assert.Equal("Original approval", changeSets[1].ReviewComment);

            var savedGrant = await verification.PublicationGrants.AsNoTracking().SingleAsync();
            Assert.True(savedGrant.CanView);
            Assert.False(savedGrant.CanUpdate);
            Assert.False(savedGrant.CanApprove);

            var claimStore = new SqlPublicationChangeSetClaimStore(fixture.Factory);
            var claim = await claimStore.ClaimNextAsync(
                "integration-worker",
                Now.AddMinutes(11),
                TimeSpan.FromMinutes(10),
                CancellationToken.None);
            Assert.Null(claim);
        }
        finally
        {
            await DeleteDatabaseAsync(fixture.Factory);
        }
    }

    private static async Task<(Publication Publication, PublicationGrant Grant)> SeedEditorAsync(
        THubDbContext db)
    {
        var connection = new DataConnection(
            "Grant revocation integration source",
            ConnectionKind.SqlServer,
            "{}",
            "integration-test",
            Now);
        var applyConnection = new DataConnection(
            "Grant revocation integration apply",
            ConnectionKind.SqlServer,
            "{}",
            "integration-test",
            Now);
        var publication = new Publication(
            Guid.NewGuid(),
            "grant-revocation-editor",
            "Grant revocation editor",
            PublicationKind.Editor,
            "integration-test",
            Now);
        var role = new AccessRole(
            PublicationRole.Operator,
            "Integration operator",
            "Integration-test publication role.",
            null,
            Now,
            "integration-test");
        db.AddRange(connection, applyConnection, publication, role);
        await db.SaveChangesAsync();

        var versionId = Guid.NewGuid();
        var version = new PublicationVersion(
            versionId,
            publication.Id,
            1,
            connection.Id,
            "dbo",
            "Orders",
            PublicationSourceObjectKind.Table,
            "integration-schema-v1",
            PublicationConcurrencyMode.OriginalValues,
            new PublicationVersionSettings(defaultPageSize: 25, maximumPageSize: 100),
            [
                new PublicationColumn(
                    Guid.NewGuid(),
                    versionId,
                    0,
                    "OrderId",
                    "id",
                    PublicationDataType.Int32,
                    "int",
                    isNullable: false,
                    isReadable: true,
                    isFilterable: true,
                    isSortable: true,
                    isWritable: false,
                    isKey: true,
                    keyOrdinal: 0),
                new PublicationColumn(
                    Guid.NewGuid(),
                    versionId,
                    1,
                    "Name",
                    "name",
                    PublicationDataType.String,
                    "nvarchar(200)",
                    isNullable: false,
                    isReadable: true,
                    isFilterable: true,
                    isSortable: true,
                    isWritable: true,
                    maximumLength: 200),
            ],
            "integration-test",
            Now,
            applyConnection.Id);
        db.PublicationVersions.Add(version);
        await db.SaveChangesAsync();
        publication.ActivateVersion(version, "integration-test", Now.AddMinutes(1));
        await db.SaveChangesAsync();

        var grant = new PublicationGrant(
            Guid.NewGuid(),
            publication.Id,
            PublicationRole.Operator,
            canView: true,
            canInsert: false,
            canUpdate: true,
            canDelete: false,
            canApprove: true);
        var pending = CreateChangeSet(
            publication.Id,
            version.Id,
            "DOMAIN\\pending-editor",
            Now.AddMinutes(2));
        var approved = CreateChangeSet(
            publication.Id,
            version.Id,
            "DOMAIN\\approved-editor",
            Now.AddMinutes(3));
        approved.Approve("DOMAIN\\approver", Now.AddMinutes(4), "Original approval");
        db.AddRange(grant, pending, approved);
        await db.SaveChangesAsync();
        return (publication, grant);
    }

    private static PublicationChangeSet CreateChangeSet(
        Guid publicationId,
        Guid versionId,
        string submittedBy,
        DateTimeOffset submittedAtUtc)
    {
        var changeSetId = Guid.NewGuid();
        return new PublicationChangeSet(
            changeSetId,
            publicationId,
            versionId,
            [
                new PublicationChange(
                    Guid.NewGuid(),
                    changeSetId,
                    PublicationChangeOperation.Update,
                    "{\"id\":1}",
                    "{\"id\":1,\"name\":\"Before\"}",
                    "{\"name\":\"After\"}"),
            ],
            submittedBy,
            submittedAtUtc);
    }

    private static TestFixture CreateFixture()
    {
        var databaseName = $"THub_PublicationGrants_{Guid.NewGuid():N}";
        var connectionString =
            $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Integrated Security=true;Encrypt=false";
        var options = new DbContextOptionsBuilder<THubDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        return new TestFixture(new TestDbContextFactory(options));
    }

    private static async Task DeleteDatabaseAsync(TestDbContextFactory factory)
    {
        await using var cleanup = factory.CreateDbContext();
        await cleanup.Database.EnsureDeletedAsync();
    }

    private static async Task<Guid> FindActiveVersionIdAsync(
        TestDbContextFactory factory,
        Guid publicationId)
    {
        await using var db = factory.CreateDbContext();
        return await db.Publications
            .Where(publication => publication.Id == publicationId)
            .Select(publication => publication.ActiveVersionId!.Value)
            .SingleAsync();
    }

    private sealed record TestFixture(TestDbContextFactory Factory);

    private sealed class TestDbContextFactory(DbContextOptions<THubDbContext> options)
        : IDbContextFactory<THubDbContext>
    {
        public THubDbContext CreateDbContext() => new(options);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
