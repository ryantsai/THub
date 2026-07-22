using THub.Application.Publications;
using THub.Domain.Publications;

namespace THub.Application.Tests;

internal static class PublicationTestData
{
    public static readonly DateTimeOffset Now = new(2026, 7, 23, 8, 0, 0, TimeSpan.Zero);

    public static (Publication Publication, PublicationVersion Version) CreateActiveRestPublication()
    {
        var publication = new Publication(
            Guid.NewGuid(),
            "orders-api",
            "Orders API",
            PublicationKind.RestApi,
            "CONTOSO\\author",
            Now);
        var version = CreateVersion(publication.Id, writable: false, withForeignKey: false);
        publication.ActivateVersion(version, "CONTOSO\\author", Now);
        return (publication, version);
    }

    public static (Publication Publication, PublicationVersion Version) CreateActiveEditorPublication(
        bool withForeignKey = false)
    {
        var publication = new Publication(
            Guid.NewGuid(),
            "orders-editor",
            "Orders Editor",
            PublicationKind.Editor,
            "CONTOSO\\author",
            Now);
        var version = CreateVersion(publication.Id, writable: true, withForeignKey);
        publication.ActivateVersion(version, "CONTOSO\\author", Now);
        return (publication, version);
    }

    public static PublicationVersion CreateVersion(
        Guid publicationId,
        bool writable,
        bool withForeignKey)
    {
        var versionId = Guid.NewGuid();
        var columns = new List<PublicationColumn>
        {
            new(
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
            new(
                Guid.NewGuid(),
                versionId,
                1,
                "Name",
                "name",
                PublicationDataType.String,
                "nvarchar",
                isNullable: false,
                isReadable: true,
                isFilterable: true,
                isSortable: true,
                isWritable: writable,
                maximumLength: 200),
        };
        if (withForeignKey)
        {
            columns.Add(new PublicationColumn(
                Guid.NewGuid(),
                versionId,
                2,
                "DepartmentId",
                "departmentId",
                PublicationDataType.Int32,
                "int",
                isNullable: false,
                isReadable: true,
                isFilterable: true,
                isSortable: true,
                isWritable: writable,
                foreignKey: new PublicationForeignKey(
                    "FK_Order_Department",
                    0,
                    1,
                    "dbo",
                    "Department",
                    "DepartmentId",
                    "DisplayName",
                    ["DisplayName"],
                    PublicationLookupMode.ServerFiltered)));
        }

        return new PublicationVersion(
            versionId,
            publicationId,
            1,
            Guid.NewGuid(),
            "dbo",
            "Orders",
            PublicationSourceObjectKind.Table,
            "schema-v1",
            writable ? PublicationConcurrencyMode.OriginalValues : PublicationConcurrencyMode.ReadOnly,
            new PublicationVersionSettings(defaultPageSize: 25, maximumPageSize: 100, editorWindowSize: 50),
            columns,
            "CONTOSO\\author",
            Now);
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class FakePublicationCatalogStore : IPublicationCatalogStore
{
    public List<Publication> Publications { get; } = [];

    public List<PublicationVersion> Versions { get; } = [];

    public PublicationCatalogWriteStatus AddPublicationStatus { get; set; } =
        PublicationCatalogWriteStatus.Saved;

    public PublicationCatalogWriteStatus AddVersionStatus { get; set; } =
        PublicationCatalogWriteStatus.Saved;

    public PublicationCatalogWriteStatus UpdateStatus { get; set; } =
        PublicationCatalogWriteStatus.Saved;

    public Task<IReadOnlyList<Publication>> ListAsync(
        PublicationCatalogQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<Publication> result = Publications
            .Where(publication => query.Kind is null || publication.Kind == query.Kind)
            .Where(publication => query.State is null || publication.State == query.State)
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<Publication?> FindAsync(Guid publicationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Publications.SingleOrDefault(publication => publication.Id == publicationId));
    }

    public Task<Publication?> FindBySlugAsync(string slug, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Publications.SingleOrDefault(publication => publication.Slug == slug));
    }

    public Task<IReadOnlyList<PublicationVersion>> ListVersionsAsync(
        Guid publicationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<PublicationVersion> result = Versions
            .Where(version => version.PublicationId == publicationId)
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<PublicationVersion?> FindVersionAsync(
        Guid publicationId,
        Guid versionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Versions.SingleOrDefault(version =>
            version.PublicationId == publicationId && version.Id == versionId));
    }

    public Task<int> GetNextVersionNumberAsync(
        Guid publicationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var next = Versions
            .Where(version => version.PublicationId == publicationId)
            .Select(version => version.VersionNumber)
            .DefaultIfEmpty()
            .Max() + 1;
        return Task.FromResult(next);
    }

    public Task<PublicationCatalogWriteStatus> AddPublicationAsync(
        Publication publication,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (AddPublicationStatus == PublicationCatalogWriteStatus.Saved)
        {
            Publications.Add(publication);
        }

        return Task.FromResult(AddPublicationStatus);
    }

    public Task<PublicationCatalogWriteStatus> AddVersionAsync(
        PublicationVersion version,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (AddVersionStatus == PublicationCatalogWriteStatus.Saved)
        {
            Versions.Add(version);
        }

        return Task.FromResult(AddVersionStatus);
    }

    public Task<PublicationCatalogWriteStatus> UpdatePublicationAsync(
        Publication publication,
        DateTimeOffset expectedUpdatedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UpdateStatus);
    }
}

internal sealed class FakePublicationTokenStore : IPublicationTokenStore
{
    public List<PublicationAccessToken> Tokens { get; } = [];

    public PublicationTokenWriteStatus AddStatus { get; set; } = PublicationTokenWriteStatus.Saved;

    public PublicationTokenRevocationStatus RevokeStatus { get; set; } =
        PublicationTokenRevocationStatus.Revoked;

    public PublicationAcceptedUseStatus MeterStatus { get; set; } =
        PublicationAcceptedUseStatus.Recorded;

    public int MeterCalls { get; private set; }

    public Task<PublicationAccessToken?> FindBySelectorAsync(
        string selector,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Tokens.SingleOrDefault(token => token.Selector == selector));
    }

    public Task<IReadOnlyList<PublicationAccessToken>> ListAsync(
        Guid publicationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<PublicationAccessToken> result = Tokens
            .Where(token => token.PublicationId == publicationId)
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<PublicationTokenWriteStatus> AddAsync(
        PublicationAccessToken accessToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (AddStatus == PublicationTokenWriteStatus.Saved)
        {
            Tokens.Add(accessToken);
        }

        return Task.FromResult(AddStatus);
    }

    public Task<PublicationTokenRevocationStatus> RevokeAsync(
        Guid publicationId,
        Guid tokenId,
        string revokedBy,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (RevokeStatus == PublicationTokenRevocationStatus.Revoked)
        {
            Tokens.Single(token => token.PublicationId == publicationId && token.Id == tokenId)
                .Revoke(revokedBy, revokedAtUtc);
        }

        return Task.FromResult(RevokeStatus);
    }

    public Task<PublicationAcceptedUseStatus> TryRecordAcceptedUseAsync(
        Guid tokenId,
        Guid publicationId,
        Guid publicationVersionId,
        DateTimeOffset acceptedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MeterCalls++;
        if (MeterStatus == PublicationAcceptedUseStatus.Recorded)
        {
            Tokens.Single(token => token.Id == tokenId && token.PublicationId == publicationId)
                .RecordAcceptedRequest(acceptedAtUtc);
        }

        return Task.FromResult(MeterStatus);
    }
}

internal sealed class FakePublicationGrantStore : IPublicationGrantStore
{
    public List<PublicationGrant> Grants { get; } = [];

    public Task<IReadOnlyList<PublicationGrant>> ListAsync(
        Guid publicationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<PublicationGrant> result = Grants
            .Where(grant => grant.PublicationId == publicationId)
            .ToArray();
        return Task.FromResult(result);
    }
}

internal sealed class FakePublicationSourceDataReader : IPublicationSourceDataReader
{
    public PublicationSourceReadQuery? LastReadQuery { get; private set; }

    public PublicationForeignKeySourceQuery? LastLookupQuery { get; private set; }

    public IReadOnlyList<PublicationForeignKeyTuple>? LastResolutionTuples { get; private set; }

    public PublicationSourceReadResult<PublicationSourceRowPage> RowResult { get; set; } =
        new(PublicationSourceReadStatus.Success, new PublicationSourceRowPage([], null));

    public PublicationSourceReadResult<PublicationSourceLookupPage> LookupResult { get; set; } =
        new(PublicationSourceReadStatus.Success, new PublicationSourceLookupPage([], null));

    public PublicationSourceReadResult<PublicationSourceForeignKeyResolution> ResolutionResult { get; set; } =
        new(PublicationSourceReadStatus.Success, new PublicationSourceForeignKeyResolution([]));

    public Task<PublicationSourceReadResult<PublicationSourceRowPage>> ReadRowsAsync(
        PublicationVersion version,
        PublicationSourceReadQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastReadQuery = query;
        return Task.FromResult(RowResult);
    }

    public Task<PublicationSourceReadResult<PublicationSourceLookupPage>> ReadForeignKeyLookupAsync(
        PublicationVersion version,
        PublicationColumn column,
        PublicationForeignKeySourceQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastLookupQuery = query;
        return Task.FromResult(LookupResult);
    }

    public Task<PublicationSourceReadResult<PublicationSourceForeignKeyResolution>> ResolveForeignKeysAsync(
        PublicationVersion version,
        IReadOnlyList<PublicationForeignKeyTuple> tuples,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastResolutionTuples = tuples;
        return Task.FromResult(ResolutionResult);
    }
}

internal sealed class FakePublicationChangeSetStore : IPublicationChangeSetStore
{
    public List<PublicationChangeSet> ChangeSets { get; } = [];

    public PublicationChangeSetWriteStatus AddStatus { get; set; } =
        PublicationChangeSetWriteStatus.Saved;

    public PublicationChangeSetWriteStatus UpdateStatus { get; set; } =
        PublicationChangeSetWriteStatus.Saved;

    public string? LastAddGrantFingerprint { get; private set; }

    public string? LastUpdateGrantFingerprint { get; private set; }

    public Task<PublicationChangeSet?> FindAsync(
        Guid publicationId,
        Guid changeSetId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ChangeSets.SingleOrDefault(changeSet =>
            changeSet.PublicationId == publicationId && changeSet.Id == changeSetId));
    }

    public Task<PublicationChangeSetWriteStatus> AddAsync(
        PublicationChangeSet changeSet,
        string expectedGrantFingerprint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastAddGrantFingerprint = expectedGrantFingerprint;
        if (AddStatus == PublicationChangeSetWriteStatus.Saved)
        {
            ChangeSets.Add(changeSet);
        }

        return Task.FromResult(AddStatus);
    }

    public Task<PublicationChangeSetWriteStatus> UpdateAsync(
        PublicationChangeSet changeSet,
        DateTimeOffset expectedUpdatedAtUtc,
        string expectedGrantFingerprint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastUpdateGrantFingerprint = expectedGrantFingerprint;
        return Task.FromResult(UpdateStatus);
    }
}
