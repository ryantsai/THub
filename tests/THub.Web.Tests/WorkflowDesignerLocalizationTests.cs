using System.Text.Json;
using System.Xml.Linq;
using THub.Application.Execution;
using THub.Domain.Workflows;
using THub.Web.Components.WorkflowDesigner;

namespace THub.Web.Tests;

public sealed class WorkflowDesignerLocalizationTests
{
    private static readonly string[] TransformKeys =
    [
        "Calculated columns",
        "Aggregate rows",
        "Distinct rows",
        "Sort rows",
        "Union rows",
        "Join type",
        "Full outer",
        "Right outer",
        "Match columns",
        "Add key pair",
        "Group by",
        "Aggregate columns",
        "Sort keys",
        "Keep all rows",
        "Remove duplicate rows",
        "Match columns by name",
        "Match columns by position",
        "Maximum buffered rows",
        "Graphical editing is unavailable",
        "Settings need attention: {0}",
        "Transform settings need attention.",
        "Enter a value or choose a null operation.",
        "Enter a valid value for this column type.",
        "Remove link from {0}",
    ];

    [Fact]
    public void TransformResources_AreCompleteAndMirroredInTaiwanChinese()
    {
        var root = FindRepositoryRoot();
        var neutral = ReadResx(
            Path.Combine(
                root,
                "src",
                "THub.Web",
                "Resources",
                "Localization",
                "SharedResource.resx"));
        var taiwan = ReadResx(
            Path.Combine(
                root,
                "src",
                "THub.Web",
                "Resources",
                "Localization",
                "SharedResource.zh-TW.resx"));
        using var mirrorDocument = JsonDocument.Parse(
            File.ReadAllText(
                Path.Combine(
                    root,
                    "src",
                    "THub.Web",
                    "wwwroot",
                    "locales",
                    "zh-TW.json")));
        var mirror = mirrorDocument.RootElement;

        foreach (var key in TransformKeys)
        {
            Assert.True(neutral.TryGetValue(key, out var neutralValue), key);
            Assert.False(string.IsNullOrWhiteSpace(neutralValue));
            Assert.True(taiwan.TryGetValue(key, out var taiwanValue), key);
            Assert.False(string.IsNullOrWhiteSpace(taiwanValue));
            Assert.True(mirror.TryGetProperty(key, out var mirrorValue), key);
            Assert.Equal(taiwanValue, mirrorValue.GetString());
        }

        Assert.Equal("運算", taiwan["Operation"]);
        Assert.Equal("來源欄位", taiwan["Source column"]);
        Assert.Equal("移除", taiwan["Remove"]);
        Assert.Equal("值", taiwan["Value"]);
        Assert.Equal(
            "精確合併兩個資料流",
            taiwan["Combine exactly two streams"]);
        Assert.Equal("雙輸入聯結", taiwan["Two-input join"]);
    }

    [Fact]
    public void TransformEditorAndDesigner_ExposeEveryOperationalTransformGraphically()
    {
        var root = FindRepositoryRoot();
        var editor = File.ReadAllText(
            Path.Combine(
                root,
                "src",
                "THub.Web",
                "Components",
                "WorkflowDesigner",
                "TransformEditor.razor"));
        var designer = File.ReadAllText(
            Path.Combine(
                root,
                "src",
                "THub.Web",
                "Components",
                "Pages",
                "Designer.razor"));

        foreach (var kind in TransformKinds)
        {
            Assert.Contains(
                $"case {EditorSettingsType(kind)} ",
                editor,
                StringComparison.Ordinal);
            Assert.Contains(
                $"WorkflowNodeKind.{kind}",
                designer,
                StringComparison.Ordinal);
        }

        Assert.Contains("<TransformEditor ", designer, StringComparison.Ordinal);
        Assert.Contains(
            """<details class="advanced-settings">""",
            designer,
            StringComparison.Ordinal);
        Assert.Contains(
            "TransformInputSynchronizer.SynchronizeJoin",
            designer,
            StringComparison.Ordinal);
        Assert.Contains(
            "TransformInputSynchronizer.SynchronizeUnion",
            designer,
            StringComparison.Ordinal);
        Assert.Contains(
            "SynchronizeTransformInputs(affectedNodeIds)",
            designer,
            StringComparison.Ordinal);
        Assert.Contains(
            "SynchronizeTransformInputs(SelectedNode)",
            designer,
            StringComparison.Ordinal);
        Assert.Contains(
            "SynchronizeTransformInputs(node)",
            designer,
            StringComparison.Ordinal);
        Assert.Contains(
            "WorkflowTabularSchemaService",
            designer,
            StringComparison.Ordinal);
        Assert.Contains(
            "TabularSchemaService.Resolve(graph, inputNodeIds, knownSchemas)",
            designer,
            StringComparison.Ordinal);
        Assert.DoesNotContain("edge.From ==", designer, StringComparison.Ordinal);
        Assert.DoesNotContain("edge.To ==", designer, StringComparison.Ordinal);
        Assert.DoesNotContain(
            """Localizer["Settings need attention: {0}", ValidationError]""",
            editor,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ValidationError", editor, StringComparison.Ordinal);
        Assert.Contains(
            "aria-invalid=\"@FilterValueAriaInvalid(condition)\"",
            editor,
            StringComparison.Ordinal);
        Assert.Contains(
            "aria-describedby=\"@FilterValueAriaDescribedBy(condition)\"",
            editor,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "class=\"field-error\" role=\"alert\"",
            editor,
            StringComparison.Ordinal);
        Assert.True(
            editor.Split(
                "RevalidateFilterDrafts();",
                StringSplitOptions.None).Length >= 3,
            "Filter drafts must be revalidated both after parsing and when schema parameters change.");
    }

    [Fact]
    public void EdgeSetUsesCaseInsensitiveIdentityForCapacityDeletionAndSynchronization()
    {
        var edges = new List<DesignerEdge>
        {
            new("Source", "Join"),
            new("SOURCE", "JOIN"),
            new("Lookup", "join"),
            new("Join", "Target"),
        };

        Assert.Equal(2, DesignerEdgeSet.DistinctIncomingCount(edges, "JOIN"));
        Assert.True(DesignerEdgeSet.Contains(edges, "source", "join"));
        Assert.Equal(
            ["Source", "Lookup"],
            DesignerEdgeSet.Incoming(edges, "jOiN"));
        Assert.Equal(
            ["Target"],
            DesignerEdgeSet.Downstream(edges, "JOIN"));

        DesignerEdgeSet.RemoveIncident(edges, "SoUrCe");

        Assert.DoesNotContain(
            edges,
            edge => string.Equals(
                edge.From,
                "Source",
                StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    edge.To,
                    "Source",
                    StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(TabularDataType.Double, "1.25e3", JsonValueKind.Number)]
    [InlineData(TabularDataType.Boolean, "true", JsonValueKind.True)]
    [InlineData(TabularDataType.Int64, "-42", JsonValueKind.Number)]
    [InlineData(TabularDataType.Decimal, "1,234.50", JsonValueKind.Number)]
    [InlineData(TabularDataType.DateTimeOffset, "2026-07-26T12:30:00+08:00", JsonValueKind.String)]
    [InlineData(TabularDataType.Guid, "11111111-1111-1111-1111-111111111111", JsonValueKind.String)]
    [InlineData(TabularDataType.Binary, "AQID", JsonValueKind.String)]
    [InlineData(TabularDataType.String, "plain text", JsonValueKind.String)]
    public void FilterScalarParserMatchesRuntimeTypes(
        TabularDataType dataType,
        string text,
        JsonValueKind expectedKind)
    {
        var result = FilterScalarDraftParser.Parse(
            text,
            new TabularColumn("Value", dataType, false));

        Assert.True(result.IsValid, result.ErrorCode);
        Assert.Equal(expectedKind, result.Value.ValueKind);
        if (dataType == TabularDataType.Double)
        {
            Assert.Equal(1250d, result.Value.GetDouble());
        }
    }

    [Fact]
    public void FilterScalarParserPreservesInvalidTypedTextAndNullableEmpty()
    {
        var invalid = FilterScalarDraftParser.Parse(
            "not-a-number",
            new TabularColumn("Amount", TabularDataType.Decimal, false));
        var nullableEmpty = FilterScalarDraftParser.Parse(
            string.Empty,
            new TabularColumn("When", TabularDataType.DateTimeOffset, true));
        var nonFinite = FilterScalarDraftParser.Parse(
            "Infinity",
            new TabularColumn("Metric", TabularDataType.Double, false));

        Assert.False(invalid.IsValid);
        Assert.Equal("filter.value.decimal", invalid.ErrorCode);
        Assert.Equal("not-a-number", invalid.Value.GetString());
        Assert.True(nullableEmpty.IsValid);
        Assert.Equal(JsonValueKind.Null, nullableEmpty.Value.ValueKind);
        Assert.False(nonFinite.IsValid);
        Assert.Equal("filter.value.double", nonFinite.ErrorCode);
        Assert.Equal("Infinity", nonFinite.Value.GetString());
    }

    [Fact]
    public void FilterRevalidationPreservesExistingNullForNullableString()
    {
        var parsed = TransformEditorSettingsCodec.Parse(
            WorkflowNodeKind.FilterRows,
            """{"conditions":[{"column":"Comment","operator":"equals","value":null}]}""");
        var settings = Assert.IsType<FilterRowsEditorSettings>(parsed.Settings);
        var row = Assert.Single(settings.Conditions);

        var revalidated = FilterScalarDraftParser.Revalidate(
            row.Value!.Value,
            new TabularColumn("Comment", TabularDataType.String, true));
        row.Value = revalidated.Value;
        row.Operator = "notEquals";
        var candidate = TransformEditorSettingsCodec.Serialize(settings);

        Assert.True(revalidated.IsValid, revalidated.ErrorCode);
        Assert.Equal(JsonValueKind.Null, revalidated.Value.ValueKind);
        Assert.True(candidate.IsSuccess, candidate.Error);
        Assert.Contains(
            "\"operator\":\"notEquals\",\"value\":null",
            candidate.SettingsJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StableStringRowKeysMoveWithRowsAndResetForNewParsedModel()
    {
        var keys = new StableEditorRowKeys();
        keys.Reset(3);
        var original = Enumerable.Range(0, 3).Select(keys.Get).ToArray();

        keys.Move(0, 2);
        keys.RemoveAt(1);
        keys.Add();

        Assert.Equal(original[1], keys.Get(0));
        Assert.Equal(original[0], keys.Get(1));
        Assert.NotEqual(original[2], keys.Get(2));

        keys.Reset(2);

        Assert.Equal(2, keys.Count);
        Assert.DoesNotContain(keys.Get(0), original);
        Assert.DoesNotContain(keys.Get(1), original);
    }

    [Fact]
    public void ResponsiveInspectorUsesContainerQueryAndAccessibleStatusContrast()
    {
        var root = FindRepositoryRoot();
        var css = File.ReadAllText(
            Path.Combine(
                root,
                "src",
                "THub.Web",
                "Components",
                "WorkflowDesigner",
                "TransformEditor.razor.css"));

        Assert.Contains("container-type: inline-size", css, StringComparison.Ordinal);
        Assert.Contains("@container (max-width: 380px)", css, StringComparison.Ordinal);
        Assert.DoesNotContain("@media (max-width: 440px)", css, StringComparison.Ordinal);
        Assert.Contains("--editor-muted: #586762", css, StringComparison.Ordinal);
        Assert.True(
            css.Split(".join-row .equals", StringSplitOptions.None).Length >= 3,
            "Both the container query and fallback must hide the Join equals sign.");
    }

    private static readonly WorkflowNodeKind[] TransformKinds =
    [
        WorkflowNodeKind.SelectColumns,
        WorkflowNodeKind.FilterRows,
        WorkflowNodeKind.Join,
        WorkflowNodeKind.UnionRows,
        WorkflowNodeKind.DeriveColumns,
        WorkflowNodeKind.AggregateRows,
        WorkflowNodeKind.DistinctRows,
        WorkflowNodeKind.SortRows,
    ];

    private static string EditorSettingsType(WorkflowNodeKind kind) => kind switch
    {
        WorkflowNodeKind.SelectColumns => "SelectColumnsEditorSettings",
        WorkflowNodeKind.FilterRows => "FilterRowsEditorSettings",
        WorkflowNodeKind.Join => "JoinEditorSettings",
        WorkflowNodeKind.UnionRows => "UnionRowsEditorSettings",
        WorkflowNodeKind.DeriveColumns => "DeriveColumnsEditorSettings",
        WorkflowNodeKind.AggregateRows => "AggregateRowsEditorSettings",
        WorkflowNodeKind.DistinctRows => "DistinctRowsEditorSettings",
        WorkflowNodeKind.SortRows => "SortRowsEditorSettings",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static IReadOnlyDictionary<string, string> ReadResx(string path) =>
        XDocument.Load(path)
            .Root!
            .Elements("data")
            .ToDictionary(
                element => element.Attribute("name")!.Value,
                element => element.Element("value")?.Value ?? string.Empty,
                StringComparer.Ordinal);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "THub.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the THub repository root.");
    }
}
