using System.Text.Json;
using THub.Application.Publications;
using THub.Domain.Publications;
using THub.Web.Publications;

namespace THub.Web.Tests;

public sealed class PublicationSpreadsheetChangeMapperTests
{
    [Fact]
    public void Build_UpdatesOnlyMutableNonKeyColumnsAndKeepsConcurrencySnapshot()
    {
        var version = CreateVersion(
            KeyColumn(),
            StringColumn("name", writable: true),
            RowVersionColumn());
        var original = Values(("id", 7), ("name", "before"), ("version", new byte[] { 1, 2 }));
        var current = Values(("id", 99), ("name", "after"), ("version", Convert.ToBase64String([1, 2])));

        var result = PublicationSpreadsheetChangeMapper.Build(
            version,
            [new PublicationSpreadsheetRow(original, current, false, false)]);

        Assert.True(result.IsSuccess, result.Error);
        var change = Assert.Single(result.Changes);
        Assert.Equal(PublicationChangeOperation.Update, change.Operation);
        Assert.Equal(7, Read(change.KeyJson!, "id").GetInt32());
        Assert.Equal("after", Read(change.AfterJson!, "name").GetString());
        Assert.False(JsonDocument.Parse(change.AfterJson!).RootElement.TryGetProperty("id", out _));
        Assert.Equal("AQI=", Read(change.BeforeJson!, "version").GetString());
    }

    [Fact]
    public void Build_InsertIncludesNonGeneratedNaturalKey()
    {
        var version = CreateVersion(KeyColumn(), StringColumn("name", writable: true));
        var row = Values(("id", "42"), ("name", "new row"));

        var result = PublicationSpreadsheetChangeMapper.Build(
            version,
            [new PublicationSpreadsheetRow(null, row, true, false)]);

        Assert.True(result.IsSuccess, result.Error);
        var change = Assert.Single(result.Changes);
        Assert.Equal(PublicationChangeOperation.Insert, change.Operation);
        Assert.Equal(42, Read(change.AfterJson!, "id").GetInt32());
        Assert.Equal("new row", Read(change.AfterJson!, "name").GetString());
    }

    [Fact]
    public void Build_InvalidTypedCellFailsBeforeStaging()
    {
        var version = CreateVersion(KeyColumn(), StringColumn("name", writable: true));
        var row = Values(("id", "not-an-integer"), ("name", "new row"));

        var result = PublicationSpreadsheetChangeMapper.Build(
            version,
            [new PublicationSpreadsheetRow(null, row, true, false)]);

        Assert.False(result.IsSuccess);
        Assert.Empty(result.Changes);
        Assert.Contains("id", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_UnchangedExistingRowProducesNoChange()
    {
        var version = CreateVersion(KeyColumn(), StringColumn("name", writable: true));
        var original = Values(("id", 1), ("name", "same"));

        var result = PublicationSpreadsheetChangeMapper.Build(
            version,
            [new PublicationSpreadsheetRow(original, new Dictionary<string, object?>(original), false, false)]);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void Build_DecimalScaleDifferenceDoesNotCreateAChange()
    {
        var version = CreateVersion(
            KeyColumn(),
            new PublicationColumnDto(
                1,
                "Amount",
                "amount",
                PublicationDataType.Decimal,
                false,
                true,
                true,
                true,
                true,
                false,
                null,
                false,
                false,
                null));
        var original = Values(("id", 1), ("amount", 22000.00m));
        var current = Values(("id", 1), ("amount", 22000m));

        var result = PublicationSpreadsheetChangeMapper.Build(
            version,
            [new PublicationSpreadsheetRow(original, current, false, false)]);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void Build_ExpandsCompositeForeignKeyUpdateToCompleteTuple()
    {
        var version = CreateVersion(
            KeyColumn(),
            ForeignKeyColumn(1, "tenantId", 0),
            ForeignKeyColumn(2, "customerId", 1));
        var original = Values(("id", 1), ("tenantId", 10), ("customerId", 20));
        var current = Values(("id", 1), ("tenantId", 11), ("customerId", 20));

        var result = PublicationSpreadsheetChangeMapper.Build(
            version,
            [new PublicationSpreadsheetRow(original, current, false, false)]);

        Assert.True(result.IsSuccess, result.Error);
        var change = Assert.Single(result.Changes);
        Assert.Equal(11, Read(change.AfterJson!, "tenantId").GetInt32());
        Assert.Equal(20, Read(change.AfterJson!, "customerId").GetInt32());
    }

    private static PublicationVersionDto CreateVersion(params PublicationColumnDto[] columns) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        1,
        Guid.NewGuid(),
        "dbo",
        "Items",
        PublicationSourceObjectKind.Table,
        "fingerprint",
        columns.Any(column => column.IsConcurrencyToken)
            ? PublicationConcurrencyMode.RowVersion
            : PublicationConcurrencyMode.OriginalValues,
        new PublicationVersionSettings(),
        columns,
        "tester",
        DateTimeOffset.UtcNow);

    private static PublicationColumnDto KeyColumn() => new(
        0,
        "Id",
        "id",
        PublicationDataType.Int32,
        false,
        true,
        true,
        true,
        false,
        true,
        0,
        false,
        false,
        null);

    private static PublicationColumnDto StringColumn(string alias, bool writable) => new(
        1,
        "Name",
        alias,
        PublicationDataType.String,
        false,
        true,
        true,
        true,
        writable,
        false,
        null,
        false,
        false,
        null);

    private static PublicationColumnDto RowVersionColumn() => new(
        2,
        "Version",
        "version",
        PublicationDataType.Binary,
        false,
        true,
        false,
        false,
        false,
        false,
        null,
        true,
        true,
        null);

    private static PublicationColumnDto ForeignKeyColumn(
        int ordinal,
        string alias,
        int foreignKeyOrdinal) => new(
            ordinal,
            alias,
            alias,
            PublicationDataType.Int32,
            false,
            true,
            true,
            true,
            true,
            false,
            null,
            false,
            false,
            new PublicationForeignKeyDto(
                "FK_Order_Customer",
                foreignKeyOrdinal,
                2,
                "dbo",
                "Customers",
                alias,
                "DisplayName",
                ["DisplayName"],
                PublicationLookupMode.ServerFiltered));

    private static Dictionary<string, object?> Values(params (string Key, object? Value)[] values) =>
        values.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);

    private static JsonElement Read(string json, string property)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(property).Clone();
    }
}
