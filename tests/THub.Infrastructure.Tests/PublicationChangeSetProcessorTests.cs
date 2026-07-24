using System.Text.Json;
using Microsoft.Data.SqlClient;
using THub.Domain.Publications;
using THub.Infrastructure.Publications;

namespace THub.Infrastructure.Tests;

public sealed class PublicationChangeSetProcessorTests
{
    [Fact]
    public void BuildInsert_IncludesNonGeneratedNaturalKey()
    {
        var version = CreateEditorVersion();
        var columns = version.Columns.ToDictionary(
            column => column.PublicAlias,
            StringComparer.OrdinalIgnoreCase);
        using var command = new SqlCommand();

        var sql = PublicationChangeSetProcessor.BuildInsert(
            command,
            version,
            columns,
            ParseObject("{\"id\":2,\"name\":\"New\"}"));

        Assert.Contains("[OrderId]", sql, StringComparison.Ordinal);
        Assert.Contains("[Name]", sql, StringComparison.Ordinal);
        Assert.Equal(2, command.Parameters.Count);
    }

    [Fact]
    public void BuildUpdate_RejectsNaturalKeyMutation()
    {
        var version = CreateEditorVersion();
        var columns = version.Columns.ToDictionary(
            column => column.PublicAlias,
            StringComparer.OrdinalIgnoreCase);
        using var command = new SqlCommand();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            PublicationChangeSetProcessor.BuildUpdate(
                command,
                version,
                columns,
                ParseObject("{\"id\":1}"),
                ParseObject("{\"id\":1,\"name\":\"Old\"}"),
                ParseObject("{\"id\":2}")));

        Assert.Contains("key", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(command.Parameters.Cast<SqlParameter>());
    }

    [Fact]
    public void BuildUpdate_NeverPlacesKeyColumnInSetClause()
    {
        var version = CreateEditorVersion();
        var columns = version.Columns.ToDictionary(
            column => column.PublicAlias,
            StringComparer.OrdinalIgnoreCase);
        using var command = new SqlCommand();

        var sql = PublicationChangeSetProcessor.BuildUpdate(
            command,
            version,
            columns,
            ParseObject("{\"id\":1}"),
            ParseObject("{\"id\":1,\"name\":\"Old\"}"),
            ParseObject("{\"name\":\"New\"}"));

        var setClause = sql[..sql.IndexOf(" WHERE ", StringComparison.Ordinal)];
        Assert.Contains("[Name] = @set0", setClause, StringComparison.Ordinal);
        Assert.DoesNotContain("[OrderId] =", setClause, StringComparison.Ordinal);
        Assert.Contains("[OrderId] =", sql, StringComparison.Ordinal);
    }

    private static PublicationVersion CreateEditorVersion()
    {
        var versionId = Guid.NewGuid();
        return new PublicationVersion(
            versionId,
            Guid.NewGuid(),
            1,
            Guid.NewGuid(),
            "dbo",
            "Orders",
            PublicationSourceObjectKind.Table,
            "schema-fingerprint-v1",
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
            "CONTOSO\\publisher",
            new DateTimeOffset(2026, 7, 23, 8, 0, 0, TimeSpan.Zero),
            Guid.NewGuid());
    }

    private static IReadOnlyDictionary<string, JsonElement> ParseObject(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement
            .EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);
    }
}
