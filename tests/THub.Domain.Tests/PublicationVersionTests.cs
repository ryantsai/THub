using THub.Domain.Publications;

namespace THub.Domain.Tests;

public sealed class PublicationVersionTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 7, 23, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void VersionCopiesAndProtectsReviewedColumnSnapshot()
    {
        var versionId = Guid.NewGuid();
        var columns = new List<PublicationColumn>
        {
            CreateKeyColumn(versionId),
        };

        var version = CreateVersion(Guid.NewGuid(), versionId, columns);
        columns.Add(CreateReadableColumn(versionId, 1, "Name", "name"));

        Assert.Single(version.Columns);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<PublicationColumn>)version.Columns).Add(
                CreateReadableColumn(versionId, 2, "Email", "email")));
        Assert.Equal(250, version.Settings.EditorWindowSize);
    }

    [Fact]
    public void VersionRequiresDeterministicKeyAndUniquePublicAliases()
    {
        var versionId = Guid.NewGuid();
        var noKey = new[]
        {
            CreateReadableColumn(versionId, 0, "Name", "name"),
        };

        Assert.Throws<ArgumentException>(() => CreateVersion(Guid.NewGuid(), versionId, noKey));

        var duplicateAlias = new[]
        {
            CreateKeyColumn(versionId),
            CreateReadableColumn(versionId, 1, "Name", "ID"),
        };

        Assert.Throws<ArgumentException>(() => CreateVersion(Guid.NewGuid(), versionId, duplicateAlias));
    }

    [Fact]
    public void WritableVersionRequiresExplicitConcurrencyAndGeneratedRowVersion()
    {
        var versionId = Guid.NewGuid();
        var columns = new[]
        {
            CreateKeyColumn(versionId),
            CreateReadableColumn(versionId, 1, "Name", "name", isWritable: true),
        };

        Assert.Throws<ArgumentException>(() =>
            CreateVersion(
                Guid.NewGuid(),
                versionId,
                columns,
                PublicationConcurrencyMode.ReadOnly));

        var rowVersionColumns = columns.Append(
            new PublicationColumn(
                Guid.NewGuid(),
                versionId,
                2,
                "RowVersion",
                "rowVersion",
                PublicationDataType.Binary,
                "rowversion",
                isNullable: false,
                isReadable: true,
                isFilterable: false,
                isSortable: false,
                isWritable: false,
                isConcurrencyToken: true,
                isGenerated: true,
                maximumLength: 8));

        var version = CreateVersion(
            Guid.NewGuid(),
            versionId,
            rowVersionColumns,
            PublicationConcurrencyMode.RowVersion);

        Assert.Equal(PublicationConcurrencyMode.RowVersion, version.ConcurrencyMode);
        Assert.Equal(3, version.Columns.Count);
    }

    [Fact]
    public void CompositeForeignKeyMustBePublishedAsOneLogicalLookup()
    {
        var versionId = Guid.NewGuid();
        var foreignKey = new PublicationForeignKey(
            "FK_Order_TenantCustomer",
            ordinal: 0,
            columnCount: 2,
            "dbo",
            "Customer",
            "TenantId",
            "DisplayName",
            ["DisplayName"],
            PublicationLookupMode.DropDown);
        var columns = new[]
        {
            CreateKeyColumn(versionId),
            new PublicationColumn(
                Guid.NewGuid(),
                versionId,
                1,
                "TenantId",
                "tenantId",
                PublicationDataType.Guid,
                "uniqueidentifier",
                isNullable: false,
                isReadable: true,
                isFilterable: true,
                isSortable: true,
                isWritable: false,
                foreignKey: foreignKey),
        };

        Assert.Throws<ArgumentException>(() => CreateVersion(Guid.NewGuid(), versionId, columns));
    }

    [Fact]
    public void ForeignKeyRequiresAnExplicitlyApprovedSearchColumn()
    {
        var exception = Assert.Throws<ArgumentException>(() => new PublicationForeignKey(
            "FK_Order_Customer",
            0,
            1,
            "dbo",
            "Customer",
            "CustomerId",
            "DisplayName",
            [],
            PublicationLookupMode.ServerFiltered));

        Assert.Contains("explicitly approved", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApprovedForeignKeyCannotDiscloseOnlyReadableCompositeComponents()
    {
        var versionId = Guid.NewGuid();
        var columns = new[]
        {
            CreateKeyColumn(versionId),
            CreateForeignKeyColumn(versionId, 1, "TenantId", "tenantId", 0, isReadable: true),
            CreateForeignKeyColumn(versionId, 2, "CustomerId", "customerId", 1, isReadable: false),
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            CreateVersion(Guid.NewGuid(), versionId, columns));

        Assert.Contains("every component", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompositeForeignKeyRequiresAtomicWriteAndNullabilityPolicy()
    {
        var versionId = Guid.NewGuid();
        var columns = new[]
        {
            CreateKeyColumn(versionId),
            CreateForeignKeyColumn(
                versionId, 1, "TenantId", "tenantId", 0,
                isReadable: true, isWritable: true, isNullable: false),
            CreateForeignKeyColumn(
                versionId, 2, "CustomerId", "customerId", 1,
                isReadable: true, isWritable: false, isNullable: true),
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            CreateVersion(
                Guid.NewGuid(),
                versionId,
                columns,
                PublicationConcurrencyMode.OriginalValues));

        Assert.Contains("identical writable and nullable", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VersionSettingsEnforcePageRateAndEditorBounds()
    {
        Assert.Throws<ArgumentException>(() =>
            new PublicationVersionSettings(defaultPageSize: 101, maximumPageSize: 100));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PublicationVersionSettings(editorWindowSize: 1_001));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PublicationVersionSettings(requestsPerWindow: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PublicationVersionSettings(maximumConcurrentRequests: 101));
    }

    internal static PublicationVersion CreateVersion(Guid publicationId)
    {
        var versionId = Guid.NewGuid();
        return CreateVersion(publicationId, versionId, [CreateKeyColumn(versionId)]);
    }

    private static PublicationVersion CreateVersion(
        Guid publicationId,
        Guid versionId,
        IEnumerable<PublicationColumn> columns,
        PublicationConcurrencyMode concurrencyMode = PublicationConcurrencyMode.ReadOnly) =>
        new(
            versionId,
            publicationId,
            versionNumber: 1,
            Guid.NewGuid(),
            "dbo",
            "PublishedResult",
            PublicationSourceObjectKind.Table,
            "sha256:0123456789abcdef",
            concurrencyMode,
            new PublicationVersionSettings(),
            columns,
            "DOMAIN\\designer",
            CreatedAt);

    private static PublicationColumn CreateKeyColumn(Guid versionId) =>
        new(
            Guid.NewGuid(),
            versionId,
            0,
            "Id",
            "id",
            PublicationDataType.Int64,
            "bigint",
            isNullable: false,
            isReadable: true,
            isFilterable: true,
            isSortable: true,
            isWritable: false,
            isKey: true,
            keyOrdinal: 0,
            isGenerated: true);

    private static PublicationColumn CreateReadableColumn(
        Guid versionId,
        int ordinal,
        string sourceName,
        string alias,
        bool isWritable = false) =>
        new(
            Guid.NewGuid(),
            versionId,
            ordinal,
            sourceName,
            alias,
            PublicationDataType.String,
            "nvarchar",
            isNullable: true,
            isReadable: true,
            isFilterable: true,
            isSortable: true,
            isWritable: isWritable,
            maximumLength: 200);

    private static PublicationColumn CreateForeignKeyColumn(
        Guid versionId,
        int columnOrdinal,
        string sourceName,
        string alias,
        int foreignKeyOrdinal,
        bool isReadable,
        bool isWritable = false,
        bool isNullable = false) =>
        new(
            Guid.NewGuid(),
            versionId,
            columnOrdinal,
            sourceName,
            alias,
            PublicationDataType.Int32,
            "int",
            isNullable,
            isReadable,
            isFilterable: isReadable,
            isSortable: isReadable,
            isWritable,
            foreignKey: new PublicationForeignKey(
                "FK_Order_Customer",
                foreignKeyOrdinal,
                2,
                "dbo",
                "Customer",
                sourceName,
                "DisplayName",
                ["DisplayName"],
                PublicationLookupMode.ServerFiltered));
}
