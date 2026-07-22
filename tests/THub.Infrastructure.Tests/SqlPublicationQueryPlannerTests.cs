using THub.Application.Connections;
using THub.Application.Publications;
using THub.Domain.Publications;
using THub.Infrastructure.Publications;
using THub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace THub.Infrastructure.Tests;

public sealed class SqlPublicationQueryPlannerTests
{
    [Fact]
    public void BuildConnectionString_EnforcesIntegratedReadOnlySettings()
    {
        var configuration = new SqlServerConnectionConfiguration(
            "sql.internal.example",
            "Operations",
            encrypt: true,
            trustServerCertificate: false,
            connectTimeoutSeconds: 7);

        var builder = SqlPublicationSourceDataReader.BuildConnectionString(configuration);

        Assert.True(builder.IntegratedSecurity);
        Assert.True(builder.Encrypt);
        Assert.False(builder.TrustServerCertificate);
        Assert.False(builder.MultipleActiveResultSets);
        Assert.False(builder.PersistSecurityInfo);
        Assert.False(builder.Enlist);
        Assert.Equal(Microsoft.Data.SqlClient.ApplicationIntent.ReadOnly, builder.ApplicationIntent);
        Assert.Equal(string.Empty, builder.UserID);
        Assert.Equal(string.Empty, builder.Password);
        Assert.Equal(7, builder.ConnectTimeout);
    }

    [Fact]
    public void BuildWriteConnectionString_RetainsStrictIdentityButTargetsWritableReplica()
    {
        var configuration = new SqlServerConnectionConfiguration(
            "sql.internal.example",
            "Operations",
            encrypt: true,
            trustServerCertificate: false);

        var builder = SqlPublicationChangeSetProcessor.BuildWriteConnectionString(configuration);

        Assert.True(builder.IntegratedSecurity);
        Assert.Equal(Microsoft.Data.SqlClient.ApplicationIntent.ReadWrite, builder.ApplicationIntent);
        Assert.False(builder.MultipleActiveResultSets);
        Assert.False(builder.PersistSecurityInfo);
        Assert.Equal(string.Empty, builder.UserID);
        Assert.Equal(string.Empty, builder.Password);
    }

    [Fact]
    public void BuildRows_QuotesImmutableIdentifiersAndParameterizesValues()
    {
        var version = CreateVersion("Order]Data");
        var query = new PublicationSourceReadQuery(
            25,
            null,
            [new PublicationFilter("name", PublicationFilterOperator.Contains, "x%'] DROP TABLE dbo.X;--")],
            [new PublicationSort("name")]);

        var result = SqlPublicationQueryPlanner.BuildRows(version, query, 1_000);

        Assert.Equal(SqlPublicationPlanStatus.Success, result.Status);
        var plan = Assert.IsType<SqlPublicationReadPlan>(result.Plan);
        Assert.Contains("[dbo].[Order]]Data]", plan.CommandText, StringComparison.Ordinal);
        Assert.Contains("[Display]]Name] LIKE @__filter0", plan.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE", plan.CommandText, StringComparison.Ordinal);
        Assert.Contains("[Display]]Name] ASC, [OrderId] ASC", plan.CommandText, StringComparison.Ordinal);
        var filter = Assert.Single(plan.Parameters, parameter => parameter.ParameterName == "@__filter0");
        Assert.Contains("~%", Assert.IsType<string>(filter.Value), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRows_RejectsUnknownCallerAlias()
    {
        var version = CreateVersion("Orders");
        var query = new PublicationSourceReadQuery(
            25,
            null,
            [new PublicationFilter("unpublished", PublicationFilterOperator.Equal, "1")],
            []);

        var result = SqlPublicationQueryPlanner.BuildRows(version, query, 1_000);

        Assert.Equal(SqlPublicationPlanStatus.InvalidQuery, result.Status);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void Cursor_IsBoundToSchemaFiltersAndDeterministicSorts()
    {
        var version = CreateVersion("Orders");
        var filters = new[]
        {
            new PublicationFilter("name", PublicationFilterOperator.StartsWith, "A"),
        };
        var first = SqlPublicationQueryPlanner.BuildRows(
            version,
            new PublicationSourceReadQuery(10, null, filters, [new PublicationSort("name")]),
            1_000);
        var plan = first.Plan!;
        var cursor = SqlPublicationCursorCodec.Encode(
            version,
            filters,
            plan.Sorts,
            new Dictionary<string, object?>
            {
                ["name"] = "Alpha",
                ["id"] = 42,
            });

        var next = SqlPublicationQueryPlanner.BuildRows(
            version,
            new PublicationSourceReadQuery(10, cursor, filters, [new PublicationSort("name")]),
            1_000);
        var changedFilter = SqlPublicationQueryPlanner.BuildRows(
            version,
            new PublicationSourceReadQuery(
                10,
                cursor,
                [new PublicationFilter("name", PublicationFilterOperator.StartsWith, "B")],
                [new PublicationSort("name")]),
            1_000);

        Assert.Equal(SqlPublicationPlanStatus.Success, next.Status);
        Assert.Contains("@__cursor0", next.Plan!.CommandText, StringComparison.Ordinal);
        Assert.Contains("@__cursor1", next.Plan.CommandText, StringComparison.Ordinal);
        Assert.Equal(SqlPublicationPlanStatus.InvalidCursor, changedFilter.Status);
    }

    [Fact]
    public void BuildLookup_UsesOnlyForeignKeyMetadataAndParametersForSearch()
    {
        var version = CreateVersion("Orders", withForeignKey: true);
        var column = version.Columns.Single(candidate => candidate.PublicAlias == "departmentId");

        var result = SqlPublicationLookupPlanner.Build(
            version,
            column,
            new PublicationForeignKeySourceQuery(20, null, "Ops%_"));

        Assert.Equal(SqlPublicationPlanStatus.Success, result.Status);
        var plan = Assert.IsType<SqlPublicationLookupPlan>(result.Plan);
        Assert.Contains("FROM [reference].[Departments]", plan.CommandText, StringComparison.Ordinal);
        Assert.Contains("[DisplayName]", plan.CommandText, StringComparison.Ordinal);
        Assert.Contains("[DepartmentId]", plan.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("Ops", plan.CommandText, StringComparison.Ordinal);
        var search = Assert.Single(plan.Parameters, parameter => parameter.ParameterName == "@__search");
        Assert.Equal("%Ops~%~_%", search.Value);
    }

    [Fact]
    public void BuildForeignKeyResolution_BatchesAndParameterizesApprovedTuples()
    {
        var version = CreateVersion("Orders", withForeignKey: true);
        var tuples = Enumerable.Range(1, 101)
            .Select(index => new PublicationForeignKeyTuple(
                index,
                "FK_Orders_Departments",
                new Dictionary<string, object?> { ["departmentId"] = index }))
            .ToArray();

        var result = SqlPublicationForeignKeyResolutionPlanner.Build(version, tuples);

        Assert.Equal(SqlPublicationPlanStatus.Success, result.Status);
        var group = Assert.Single(result.Groups);
        Assert.Equal(2, group.Batches.Count);
        Assert.Contains("FROM (VALUES", group.Batches[0].CommandText, StringComparison.Ordinal);
        Assert.Contains("INNER JOIN [reference].[Departments] AS [target]", group.Batches[0].CommandText, StringComparison.Ordinal);
        Assert.Equal(100, group.Batches[0].Parameters.Count);
        Assert.Equal(System.Data.SqlDbType.Int, group.Batches[0].Parameters[0].SqlDbType);
    }

    [Fact]
    public void BuildForeignKeyResolution_RejectsPartialOrUnknownTupleAliases()
    {
        var version = CreateVersion("Orders", withForeignKey: true);

        var result = SqlPublicationForeignKeyResolutionPlanner.Build(
            version,
            [new PublicationForeignKeyTuple(
                0,
                "FK_Orders_Departments",
                new Dictionary<string, object?> { ["unapproved"] = 7 })]);

        Assert.Equal(SqlPublicationPlanStatus.InvalidQuery, result.Status);
        Assert.Empty(result.Groups);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ForeignKeyResolutionPlan_ReturnsOnlyExistingReferencedTuples()
    {
        var databaseName = $"THub_ForeignKeyResolution_{Guid.NewGuid():N}";
        var connectionString =
            $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Integrated Security=true;Encrypt=false";
        var options = new DbContextOptionsBuilder<THubDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        try
        {
            await using (var setup = new THubDbContext(options))
            {
                await setup.Database.MigrateAsync();
                await setup.Database.ExecuteSqlRawAsync("CREATE SCHEMA [reference];");
                await setup.Database.ExecuteSqlRawAsync("""
                    CREATE TABLE [reference].[Departments]
                    (
                        [DepartmentId] int NOT NULL CONSTRAINT [PK_Departments] PRIMARY KEY,
                        [DisplayName] nvarchar(200) NOT NULL
                    );
                    INSERT INTO [reference].[Departments] ([DepartmentId], [DisplayName])
                    VALUES (7, N'Operations');
                    """);
            }

            var version = CreateVersion("Orders", withForeignKey: true);
            var plan = SqlPublicationForeignKeyResolutionPlanner.Build(
                version,
                [
                    new PublicationForeignKeyTuple(
                        41,
                        "FK_Orders_Departments",
                        new Dictionary<string, object?> { ["departmentId"] = 7 }),
                    new PublicationForeignKeyTuple(
                        42,
                        "FK_Orders_Departments",
                        new Dictionary<string, object?> { ["departmentId"] = 999 }),
                ]);

            await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
            await connection.OpenAsync();
            var batch = Assert.Single(Assert.Single(plan.Groups).Batches);
            await using var command = connection.CreateCommand();
            command.CommandText = batch.CommandText;
            foreach (var parameter in batch.Parameters)
            {
                command.Parameters.Add(parameter);
            }

            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(41, reader.GetInt32(0));
            Assert.Equal("Operations", reader.GetString(1));
            Assert.False(await reader.ReadAsync());
        }
        finally
        {
            await using var cleanup = new THubDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    private static PublicationVersion CreateVersion(
        string objectName,
        bool withForeignKey = false)
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
                false,
                true,
                true,
                true,
                false,
                true,
                0),
            new(
                Guid.NewGuid(),
                versionId,
                1,
                "Display]Name",
                "name",
                PublicationDataType.String,
                "nvarchar(200)",
                false,
                true,
                true,
                true,
                false,
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
                false,
                true,
                true,
                true,
                false,
                foreignKey: new PublicationForeignKey(
                    "FK_Orders_Departments",
                    0,
                    1,
                    "reference",
                    "Departments",
                    "DepartmentId",
                    "DisplayName",
                    ["DisplayName"],
                    PublicationLookupMode.ServerFiltered)));
        }

        return new PublicationVersion(
            versionId,
            Guid.NewGuid(),
            1,
            Guid.NewGuid(),
            "dbo",
            objectName,
            PublicationSourceObjectKind.Table,
            "schema-fingerprint-v1",
            PublicationConcurrencyMode.ReadOnly,
            new PublicationVersionSettings(defaultPageSize: 25, maximumPageSize: 100),
            columns,
            "CONTOSO\\publisher",
            new DateTimeOffset(2026, 7, 23, 8, 0, 0, TimeSpan.Zero));
    }
}
