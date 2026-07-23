using THub.Application.Execution;
using THub.Domain.Connections;
using THub.Infrastructure.Execution;

namespace THub.Infrastructure.Tests;

public sealed class RelationalTargetMutationSqlTests
{
    [Theory]
    [InlineData(
        ConnectionKind.SqlServer,
        "UPDATE [sales].[Orders] WITH (UPDLOCK, SERIALIZABLE)",
        "IF @@ROWCOUNT = 0 BEGIN INSERT INTO [sales].[Orders]")]
    [InlineData(
        ConnectionKind.MySql,
        "INSERT INTO `sales`.`Orders`",
        "ON DUPLICATE KEY UPDATE `Name` = VALUES(`Name`)")]
    [InlineData(
        ConnectionKind.PostgreSql,
        "INSERT INTO \"sales\".\"Orders\"",
        "ON CONFLICT (\"Id\") DO UPDATE SET \"Name\" = EXCLUDED.\"Name\"")]
    [InlineData(
        ConnectionKind.Oracle,
        "MERGE INTO \"sales\".\"Orders\" target",
        "WHEN NOT MATCHED THEN INSERT (\"Id\", \"Name\")")]
    public void BuildsProviderSpecificParameterizedUpsert(
        ConnectionKind kind,
        string expectedStart,
        string expectedMutation)
    {
        var sql = RelationalTargetMutationSql.Build(
            kind,
            "sales",
            "Orders",
            "upsert",
            ["Id", "Name"],
            ["Id"]);

        Assert.StartsWith(expectedStart, sql, StringComparison.Ordinal);
        Assert.Contains(expectedMutation, sql, StringComparison.Ordinal);
        Assert.DoesNotContain("Alice", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ConnectionKind.SqlServer, "DELETE FROM [sales].[Orders] WHERE [Id] = @p0")]
    [InlineData(ConnectionKind.MySql, "DELETE FROM `sales`.`Orders` WHERE `Id` = @p0")]
    [InlineData(ConnectionKind.PostgreSql, "DELETE FROM \"sales\".\"Orders\" WHERE \"Id\" = @p0")]
    [InlineData(ConnectionKind.Oracle, "DELETE FROM \"sales\".\"Orders\" WHERE \"Id\" = :p0")]
    public void BuildsProviderSpecificKeyedDelete(ConnectionKind kind, string expected)
    {
        var sql = RelationalTargetMutationSql.Build(
            kind,
            "sales",
            "Orders",
            "delete",
            ["Id"],
            ["Id"]);

        Assert.Equal(expected, sql);
    }

    [Fact]
    public void KeyTrackerRejectsNullAndDuplicateInputKeys()
    {
        var tracker = new RelationalMutationKeyTracker();
        tracker.Add([TabularValue.From(42L)]);

        var duplicate = Assert.Throws<WorkflowNodeExecutionException>(
            () => tracker.Add([TabularValue.From(42L)]));
        var nullKey = Assert.Throws<WorkflowNodeExecutionException>(
            () => new RelationalMutationKeyTracker().Add([TabularValue.Null]));

        Assert.Equal("execution.database.target.key.duplicate", duplicate.Error.Code);
        Assert.Equal("execution.database.target.key.null", nullKey.Error.Code);
    }
}
