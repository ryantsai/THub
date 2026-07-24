using System.Data.Common;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using THub.Application.Publications;
using THub.Domain.Connections;
using THub.Domain.Publications;
using THub.Infrastructure.Publications;

namespace THub.Infrastructure.Tests;

public sealed class RelationalPublicationPlannerTests
{
    [Theory]
    [InlineData(ConnectionKind.MySql, "`sales`.`Order Data`", "LIMIT 26", "@__filter0")]
    [InlineData(ConnectionKind.PostgreSql, "\"sales\".\"Order Data\"", "LIMIT 26", "@__filter0")]
    [InlineData(ConnectionKind.Oracle, "\"sales\".\"Order Data\"", "FETCH FIRST 26 ROWS ONLY", ":__filter0")]
    public void BuildRows_QuotesProviderIdentifiersAndParameterizesCallerValues(
        ConnectionKind kind,
        string qualifiedName,
        string pagingClause,
        string parameterMarker)
    {
        var version = CreateVersion(PublicationConcurrencyMode.ReadOnly);
        var query = new PublicationSourceReadQuery(
            25,
            null,
            [new PublicationFilter("name", PublicationFilterOperator.Contains, "x%'; DROP TABLE Orders;--")],
            [new PublicationSort("name")]);

        var result = RelationalPublicationQueryPlanner.BuildRows(kind, version, query, 1_000);

        Assert.Equal(SqlPublicationPlanStatus.Success, result.Status);
        var plan = Assert.IsType<RelationalPublicationReadPlan>(result.Plan);
        Assert.Contains(qualifiedName, plan.CommandText, StringComparison.Ordinal);
        Assert.Contains(pagingClause, plan.CommandText, StringComparison.Ordinal);
        Assert.Contains(parameterMarker, plan.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE", plan.CommandText, StringComparison.Ordinal);
        var parameter = Assert.Single(plan.Parameters, value => value.Name == "__filter0");
        Assert.Contains("~%", Assert.IsType<string>(parameter.Value), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ConnectionKind.MySql, "`Name` = @set0", "`OrderId` = @where0")]
    [InlineData(ConnectionKind.PostgreSql, "\"Name\" = @set0", "\"OrderId\" = @where0")]
    [InlineData(ConnectionKind.Oracle, "\"Name\" = :set0", "\"OrderId\" = :where0")]
    public void BuildUpdate_UsesProviderMarkersAndNeverMutatesTheKey(
        ConnectionKind kind,
        string expectedAssignment,
        string expectedKeyPredicate)
    {
        using var command = CreateCommand(kind);
        var version = CreateVersion(PublicationConcurrencyMode.OriginalValues);
        var change = new PublicationChange(
            Guid.NewGuid(),
            Guid.NewGuid(),
            PublicationChangeOperation.Update,
            "{\"id\":1}",
            "{\"id\":1,\"name\":\"Old\"}",
            "{\"name\":\"New\"}");

        var sql = RelationalPublicationMutationBuilder.Build(command, kind, version, change);

        var setClause = sql[..sql.IndexOf(" WHERE ", StringComparison.Ordinal)];
        Assert.Contains(expectedAssignment, setClause, StringComparison.Ordinal);
        Assert.DoesNotContain("OrderId", setClause, StringComparison.Ordinal);
        Assert.Contains(expectedKeyPredicate, sql, StringComparison.Ordinal);
        Assert.Equal(3, command.Parameters.Count);
    }

    [Fact]
    public void Cursor_RemainsBoundToTheRelationalFilterAndSortContract()
    {
        var version = CreateVersion(PublicationConcurrencyMode.ReadOnly);
        var filters = new[]
        {
            new PublicationFilter("name", PublicationFilterOperator.StartsWith, "A")
        };
        var first = RelationalPublicationQueryPlanner.BuildRows(
            ConnectionKind.PostgreSql,
            version,
            new PublicationSourceReadQuery(10, null, filters, [new PublicationSort("name")]),
            1_000);
        var cursor = SqlPublicationCursorCodec.Encode(
            version,
            filters,
            first.Plan!.Sorts,
            new Dictionary<string, object?>
            {
                ["name"] = "Alpha",
                ["id"] = 42
            });

        var next = RelationalPublicationQueryPlanner.BuildRows(
            ConnectionKind.PostgreSql,
            version,
            new PublicationSourceReadQuery(10, cursor, filters, [new PublicationSort("name")]),
            1_000);

        Assert.Equal(SqlPublicationPlanStatus.Success, next.Status);
        Assert.Contains("@__cursor0", next.Plan!.CommandText, StringComparison.Ordinal);
        Assert.Contains("@__cursor1", next.Plan.CommandText, StringComparison.Ordinal);
    }

    private static DbCommand CreateCommand(ConnectionKind kind) => kind switch
    {
        ConnectionKind.MySql => new MySqlCommand(),
        ConnectionKind.PostgreSql => new NpgsqlCommand(),
        ConnectionKind.Oracle => new OracleCommand(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static PublicationVersion CreateVersion(PublicationConcurrencyMode concurrencyMode)
    {
        var versionId = Guid.NewGuid();
        return new PublicationVersion(
            versionId,
            Guid.NewGuid(),
            1,
            Guid.NewGuid(),
            "sales",
            "Order Data",
            PublicationSourceObjectKind.Table,
            "relational-schema-fingerprint-v1",
            concurrencyMode,
            new PublicationVersionSettings(defaultPageSize: 25, maximumPageSize: 100),
            [
                new PublicationColumn(
                    Guid.NewGuid(),
                    versionId,
                    0,
                    "OrderId",
                    "id",
                    PublicationDataType.Int32,
                    "integer",
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
                    "varchar(200)",
                    isNullable: false,
                    isReadable: true,
                    isFilterable: true,
                    isSortable: true,
                    isWritable: concurrencyMode != PublicationConcurrencyMode.ReadOnly,
                    maximumLength: 200)
            ],
            "CONTOSO\\publisher",
            new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero),
            concurrencyMode == PublicationConcurrencyMode.ReadOnly ? null : Guid.NewGuid());
    }
}
