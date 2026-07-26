using Microsoft.Extensions.DependencyInjection;
using THub.Application;
using THub.Application.Execution;
using THub.Application.Workflows;
using THub.Domain.Workflows;

namespace THub.Application.Tests;

public sealed class WorkflowTabularSchemaServiceTests
{
    private readonly WorkflowTabularSchemaService service =
        new(new WorkflowNodeSettingsValidator());

    [Fact]
    public void WebApplicationRegistersSchemaServiceWithWorkflowDefinitionServices()
    {
        var services = new ServiceCollection();
        services.AddWebApplication();

        Assert.Contains(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(WorkflowTabularSchemaService)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void ResolvesKnownSourceSchema()
    {
        var graph = Graph([Node("orders", WorkflowNodeKind.SqlSource)]);
        var expected = Schema(
            ("Id", TabularDataType.Int64, false),
            ("Amount", TabularDataType.Decimal, true));

        var result = service.Resolve(
            graph,
            "orders",
            Sources(("orders", expected)));

        Assert.True(result.IsSuccess);
        Assert.Same(expected, result.Schema);
    }

    [Fact]
    public void BatchResolvePreservesRequestOrderAndSharesRecursiveResults()
    {
        var graph = Graph(
            [
                Node("source", WorkflowNodeKind.SqlSource),
                Node(
                    "select",
                    WorkflowNodeKind.SelectColumns,
                    """{"columns":["Name","Id"]}"""),
                Node(
                    "filter",
                    WorkflowNodeKind.FilterRows,
                    """{"conditions":[{"column":"Id","operator":"isNotNull"}]}""")
            ],
            [Edge("source", "select"), Edge("select", "filter")]);
        var sources = Sources(("source", Schema(
            ("Id", TabularDataType.Int64, false),
            ("Name", TabularDataType.String, true))));

        var results = service.Resolve(
            graph,
            ["filter", "select", "FILTER"],
            sources);

        Assert.Equal(3, results.Count);
        AssertColumns(
            results[0],
            ("Name", TabularDataType.String, true),
            ("Id", TabularDataType.Int64, false));
        AssertColumns(
            results[1],
            ("Name", TabularDataType.String, true),
            ("Id", TabularDataType.Int64, false));
        Assert.Same(results[0], results[2]);
        Assert.Same(results[1].Schema, results[0].Schema);
    }

    [Fact]
    public void RelationalSourceUsesConfiguredColumnSelectionOrder()
    {
        var graph = Graph(
            [
                Node(
                    "orders",
                    WorkflowNodeKind.SqlSource,
                    SqlSourceSettings(["Name", "Id"]))
            ]);

        var result = service.Resolve(
            graph,
            "orders",
            Sources(("orders", Schema(
                ("Id", TabularDataType.Int64, false),
                ("Name", TabularDataType.String, true),
                ("Ignored", TabularDataType.Guid, false)))));

        AssertColumns(
            result,
            ("Name", TabularDataType.String, true),
            ("Id", TabularDataType.Int64, false));
    }

    [Theory]
    [InlineData(WorkflowNodeKind.CsvSource)]
    [InlineData(WorkflowNodeKind.ExcelSource)]
    [InlineData(WorkflowNodeKind.FtpSource)]
    public void FileSourceValidatesSettingsBeforeReturningKnownSchema(
        WorkflowNodeKind sourceKind)
    {
        var graph = Graph([Node("source", sourceKind, "{}")]);

        var result = service.Resolve(
            graph,
            "source",
            Sources(("source", Schema(("Value", TabularDataType.String, true)))));

        Assert.False(result.IsSuccess);
        Assert.Equal("schema.settings.invalid", result.Code);
        Assert.Contains("settings", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectsColumnsInConfiguredOrderAndPassthroughTransformsPreserveSchema()
    {
        var graph = Graph(
            [
                Node("source", WorkflowNodeKind.SqlSource),
                Node(
                    "select",
                    WorkflowNodeKind.SelectColumns,
                    """{"columns":["Name","Id"]}"""),
                Node(
                    "filter",
                    WorkflowNodeKind.FilterRows,
                    """{"conditions":[{"column":"Id","operator":"greaterThan","value":0}]}"""),
                Node(
                    "distinct",
                    WorkflowNodeKind.DistinctRows,
                    """{"columns":["Id"],"maximumKeys":100}"""),
                Node(
                    "sort",
                    WorkflowNodeKind.SortRows,
                    """{"keys":[{"column":"Name","direction":"ascending","nulls":"last"}],"maximumBufferedRows":100}""")
            ],
            [
                Edge("source", "select"),
                Edge("select", "filter"),
                Edge("filter", "distinct"),
                Edge("distinct", "sort")
            ]);

        var result = service.Resolve(
            graph,
            "sort",
            Sources(("source", Schema(
                ("Id", TabularDataType.Int64, false),
                ("Name", TabularDataType.String, true),
                ("Ignored", TabularDataType.Guid, false)))));

        AssertColumns(
            result,
            ("Name", TabularDataType.String, true),
            ("Id", TabularDataType.Int64, false));
    }

    [Fact]
    public void JoinUsesDeterministicCollisionNames()
    {
        var graph = Graph(
            [
                Node("left", WorkflowNodeKind.SqlSource),
                Node("right", WorkflowNodeKind.SqlSource),
                Join(
                    "join",
                    "left",
                    "right",
                    "inner",
                    ["Id"],
                    ["Id"])
            ],
            [Edge("left", "join"), Edge("right", "join")]);

        var result = service.Resolve(
            graph,
            "join",
            Sources(
                ("left", Schema(
                    ("Id", TabularDataType.Int64, false),
                    ("right.Id", TabularDataType.String, false))),
                ("right", Schema(
                    ("Id", TabularDataType.Int64, false),
                    ("RIGHT.ID", TabularDataType.String, false)))));

        AssertColumns(
            result,
            ("Id", TabularDataType.Int64, false),
            ("right.Id", TabularDataType.String, false),
            ("right.Id_2", TabularDataType.Int64, false),
            ("right.RIGHT.ID", TabularDataType.String, false));
    }

    [Fact]
    public void SharedJoinSemanticsTruncatesAndSuffixesCollidingNames()
    {
        var sourceName = new string('x', TabularColumn.MaximumNameLength);
        var truncatedPrefix = $"right.{sourceName}"[..TabularColumn.MaximumNameLength];
        var expected = $"{truncatedPrefix[..^2]}_2";
        var left = Schema(
            (sourceName, TabularDataType.Int64, false),
            (truncatedPrefix, TabularDataType.String, false));
        var right = Schema((sourceName, TabularDataType.Int64, false));

        var result = WorkflowTransformSchemaSemantics.CreateJoinSchema(
            left,
            right,
            "full");

        Assert.Equal(expected, result.Columns[^1].Name);
        Assert.Equal(TabularColumn.MaximumNameLength, result.Columns[^1].Name.Length);
        Assert.All(result.Columns, column => Assert.True(column.IsNullable));
    }

    [Theory]
    [InlineData("inner", false, false)]
    [InlineData("left", false, true)]
    [InlineData("right", true, false)]
    [InlineData("full", true, true)]
    public void JoinNullabilityMatchesJoinType(
        string joinType,
        bool leftNullable,
        bool rightNullable)
    {
        var graph = Graph(
            [
                Node("left", WorkflowNodeKind.SqlSource),
                Node("right", WorkflowNodeKind.SqlSource),
                Join("join", "left", "right", joinType, ["Id"], ["Id"])
            ],
            [Edge("left", "join"), Edge("right", "join")]);

        var result = service.Resolve(
            graph,
            "join",
            Sources(
                ("left", Schema(("Id", TabularDataType.Int64, false))),
                ("right", Schema(("Id", TabularDataType.Int64, false)))));

        AssertColumns(
            result,
            ("Id", TabularDataType.Int64, leftNullable),
            ("right.Id", TabularDataType.Int64, rightNullable));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("position")]
    public void UnionAlignsInputsAndCombinesNullability(string matchBy)
    {
        var graph = Graph(
            [
                Node("north", WorkflowNodeKind.SqlSource),
                Node("south", WorkflowNodeKind.SqlSource),
                Node(
                    "union",
                    WorkflowNodeKind.UnionRows,
                    $$"""{"inputNodeIds":["north","south"],"matchBy":"{{matchBy}}","mode":"all"}""")
            ],
            [Edge("north", "union"), Edge("south", "union")]);
        var south = matchBy == "name"
            ? Schema(
                ("Name", TabularDataType.String, false),
                ("Id", TabularDataType.Int64, true))
            : Schema(
                ("OtherId", TabularDataType.Int64, true),
                ("OtherName", TabularDataType.String, false));

        var result = service.Resolve(
            graph,
            "union",
            Sources(
                ("north", Schema(
                    ("Id", TabularDataType.Int64, false),
                    ("Name", TabularDataType.String, true))),
                ("south", south)));

        AssertColumns(
            result,
            ("Id", TabularDataType.Int64, true),
            ("Name", TabularDataType.String, true));
    }

    [Fact]
    public void SharedUnionSemanticsReturnsRuntimeAlignment()
    {
        var first = Schema(
            ("Id", TabularDataType.Int64, false),
            ("Name", TabularDataType.String, true));
        var second = Schema(
            ("name", TabularDataType.String, false),
            ("id", TabularDataType.Int64, true));

        var plan = WorkflowTransformSchemaSemantics.CreateUnionPlan(
            [first, second],
            UnionMatchMode.Name);

        Assert.Equal([0, 1], plan.Alignments[0]);
        Assert.Equal([1, 0], plan.Alignments[1]);
        AssertColumns(
            new WorkflowTabularSchemaResult(plan.Schema, null, null),
            ("Id", TabularDataType.Int64, true),
            ("Name", TabularDataType.String, true));
    }

    [Fact]
    public void DeriveColumnsAppendsConfiguredTypesAndNullability()
    {
        var graph = SingleInputGraph(
            Node(
                "derive",
                WorkflowNodeKind.DeriveColumns,
                """{"columns":[{"name":"Revenue","type":"Decimal","nullable":false,"expression":"row.Quantity * row.Price"},{"name":"Label","type":"String","nullable":true,"expression":"row.Name"}]}"""));

        var result = service.Resolve(
            graph,
            "derive",
            Sources(("source", Schema(
                ("Quantity", TabularDataType.Int64, false),
                ("Price", TabularDataType.Decimal, false)))));

        AssertColumns(
            result,
            ("Quantity", TabularDataType.Int64, false),
            ("Price", TabularDataType.Decimal, false),
            ("Revenue", TabularDataType.Decimal, false),
            ("Label", TabularDataType.String, true));
    }

    [Fact]
    public void DeriveColumnsRejectsReplacementOfExistingColumn()
    {
        var graph = SingleInputGraph(
            Node(
                "derive",
                WorkflowNodeKind.DeriveColumns,
                """{"columns":[{"name":"id","type":"String","nullable":false,"expression":"'replacement'"}]}"""));

        var result = service.Resolve(
            graph,
            "derive",
            Sources(("source", Schema(("Id", TabularDataType.Int64, false)))));

        Assert.False(result.IsSuccess);
        Assert.Equal("schema.derive.column.duplicate", result.Code);
        Assert.Contains("id", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AggregateCalculatesOutputTypesAndNullability()
    {
        var graph = SingleInputGraph(
            Node(
                "aggregate",
                WorkflowNodeKind.AggregateRows,
                """{"groupBy":["Region"],"aggregates":[{"name":"Rows","operation":"count"},{"name":"Present","operation":"countNonNull","column":"Amount"},{"name":"Revenue","operation":"sum","column":"Amount"},{"name":"MeanUnits","operation":"average","column":"Units"},{"name":"FirstSeen","operation":"minimum","column":"CreatedAt"}],"maximumGroups":100}"""));

        var result = service.Resolve(
            graph,
            "aggregate",
            Sources(("source", Schema(
                ("Region", TabularDataType.String, false),
                ("Amount", TabularDataType.Decimal, true),
                ("Units", TabularDataType.Int64, false),
                ("CreatedAt", TabularDataType.DateTimeOffset, false)))));

        AssertColumns(
            result,
            ("Region", TabularDataType.String, false),
            ("Rows", TabularDataType.Int64, false),
            ("Present", TabularDataType.Int64, false),
            ("Revenue", TabularDataType.Decimal, true),
            ("MeanUnits", TabularDataType.Decimal, true),
            ("FirstSeen", TabularDataType.DateTimeOffset, true));
    }

    [Fact]
    public void SharedAggregateSemanticsCalculatesRuntimeOutputSchema()
    {
        var input = Schema(
            ("Group", TabularDataType.String, false),
            ("Whole", TabularDataType.Int64, false),
            ("Fractional", TabularDataType.Double, true));
        var settings = new AggregateRowsNodeSettings(
            ["Group"],
            [
                new("Count", AggregateOperation.Count, null),
                new("WholeAverage", AggregateOperation.Average, "Whole"),
                new("FractionalSum", AggregateOperation.Sum, "Fractional")
            ],
            100);

        var schema = WorkflowTransformSchemaSemantics.CreateAggregateSchema(
            input,
            settings);

        AssertColumns(
            new WorkflowTabularSchemaResult(schema, null, null),
            ("Group", TabularDataType.String, false),
            ("Count", TabularDataType.Int64, false),
            ("WholeAverage", TabularDataType.Decimal, true),
            ("FractionalSum", TabularDataType.Double, true));
    }

    [Fact]
    public void ResolvesAThreeTableJoinChain()
    {
        var graph = Graph(
            [
                Node("orders", WorkflowNodeKind.SqlSource),
                Node("customers", WorkflowNodeKind.SqlSource),
                Node("regions", WorkflowNodeKind.SqlSource),
                Join(
                    "orders-customers",
                    "orders",
                    "customers",
                    "left",
                    ["CustomerId"],
                    ["Id"]),
                Join(
                    "with-regions",
                    "orders-customers",
                    "regions",
                    "inner",
                    ["RegionId"],
                    ["Id"])
            ],
            [
                Edge("orders", "orders-customers"),
                Edge("customers", "orders-customers"),
                Edge("orders-customers", "with-regions"),
                Edge("regions", "with-regions")
            ]);

        var result = service.Resolve(
            graph,
            "with-regions",
            Sources(
                ("orders", Schema(
                    ("OrderId", TabularDataType.Int64, false),
                    ("CustomerId", TabularDataType.Int64, false))),
                ("customers", Schema(
                    ("Id", TabularDataType.Int64, false),
                    ("RegionId", TabularDataType.Int64, false),
                    ("CustomerName", TabularDataType.String, false))),
                ("regions", Schema(
                    ("Id", TabularDataType.Int64, false),
                    ("RegionName", TabularDataType.String, false)))));

        AssertColumns(
            result,
            ("OrderId", TabularDataType.Int64, false),
            ("CustomerId", TabularDataType.Int64, false),
            ("Id", TabularDataType.Int64, true),
            ("RegionId", TabularDataType.Int64, true),
            ("CustomerName", TabularDataType.String, true),
            ("right.Id", TabularDataType.Int64, false),
            ("RegionName", TabularDataType.String, false));
    }

    [Fact]
    public void ReportsMissingSourceSchema()
    {
        var result = service.Resolve(
            Graph([Node("source", WorkflowNodeKind.SqlSource)]),
            "source",
            new Dictionary<string, TabularSchema>());

        Assert.False(result.IsSuccess);
        Assert.Equal("schema.source.unresolved", result.Code);
        Assert.Contains("source", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsIncompatibleUnionSchema()
    {
        var graph = Graph(
            [
                Node("one", WorkflowNodeKind.SqlSource),
                Node("two", WorkflowNodeKind.SqlSource),
                Node(
                    "union",
                    WorkflowNodeKind.UnionRows,
                    """{"inputNodeIds":["one","two"],"matchBy":"name","mode":"all"}""")
            ],
            [Edge("one", "union"), Edge("two", "union")]);

        var result = service.Resolve(
            graph,
            "union",
            Sources(
                ("one", Schema(("Id", TabularDataType.Int64, false))),
                ("two", Schema(("Id", TabularDataType.String, false)))));

        Assert.False(result.IsSuccess);
        Assert.Equal("schema.union.incompatible", result.Code);
        Assert.Contains("Id", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsCycleInReachableGraph()
    {
        var graph = Graph(
            [
                Node(
                    "one",
                    WorkflowNodeKind.SelectColumns,
                    """{"columns":["Id"]}"""),
                Node(
                    "two",
                    WorkflowNodeKind.FilterRows,
                    """{"conditions":[{"column":"Id","operator":"isNotNull"}]}""")
            ],
            [Edge("one", "two"), Edge("two", "one")]);

        var result = service.Resolve(
            graph,
            "two",
            new Dictionary<string, TabularSchema>());

        Assert.False(result.IsSuccess);
        Assert.Equal("schema.graph.cycle", result.Code);
        Assert.Contains("one", result.Message, StringComparison.Ordinal);
        Assert.Contains("two", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsMissingReferencedColumn()
    {
        var graph = SingleInputGraph(
            Node(
                "sort",
                WorkflowNodeKind.SortRows,
                """{"keys":[{"column":"Missing","direction":"ascending","nulls":"last"}],"maximumBufferedRows":100}"""));

        var result = service.Resolve(
            graph,
            "sort",
            Sources(("source", Schema(("Id", TabularDataType.Int64, false)))));

        Assert.False(result.IsSuccess);
        Assert.Equal("schema.column.unresolved", result.Code);
        Assert.Contains("Missing", result.Message, StringComparison.Ordinal);
    }

    private static WorkflowGraph SingleInputGraph(WorkflowNode transform) =>
        Graph(
            [Node("source", WorkflowNodeKind.SqlSource), transform],
            [Edge("source", transform.Id)]);

    private static WorkflowGraph Graph(
        IReadOnlyList<WorkflowNode> nodes,
        IReadOnlyList<WorkflowEdge>? edges = null) =>
        new(nodes, edges ?? []);

    private static WorkflowNode Node(
        string id,
        WorkflowNodeKind kind,
        string settings = "{}") =>
        new(
            id,
            kind,
            id,
            0,
            0,
            settings == "{}" && IsRelationalSource(kind)
                ? SqlSourceSettings()
                : settings);

    private static bool IsRelationalSource(WorkflowNodeKind kind) =>
        kind is WorkflowNodeKind.SqlSource
            or WorkflowNodeKind.MySqlSource
            or WorkflowNodeKind.PostgreSqlSource
            or WorkflowNodeKind.OracleSource;

    private static string SqlSourceSettings(IReadOnlyList<string>? columns = null)
    {
        var selection = columns is null
            ? string.Empty
            : $""","columns":[{string.Join(",", columns.Select(column => $"\"{column}\""))}]""";
        return $$"""{"connectionId":"11111111-1111-1111-1111-111111111111","schema":"dbo","object":"Source","batchSize":100{{selection}}}""";
    }

    private static WorkflowNode Join(
        string id,
        string left,
        string right,
        string type,
        IReadOnlyList<string> leftKeys,
        IReadOnlyList<string> rightKeys) =>
        Node(
            id,
            WorkflowNodeKind.Join,
            $$"""{"leftNodeId":"{{left}}","rightNodeId":"{{right}}","leftKeys":[{{string.Join(",", leftKeys.Select(key => $"\"{key}\""))}}],"rightKeys":[{{string.Join(",", rightKeys.Select(key => $"\"{key}\""))}}],"type":"{{type}}","maximumBufferedRows":100}""");

    private static WorkflowEdge Edge(string from, string to) => new(from, to);

    private static TabularSchema Schema(
        params (string Name, TabularDataType Type, bool Nullable)[] columns) =>
        new(columns.Select(column =>
            new TabularColumn(column.Name, column.Type, column.Nullable)));

    private static IReadOnlyDictionary<string, TabularSchema> Sources(
        params (string NodeId, TabularSchema Schema)[] schemas) =>
        schemas.ToDictionary(
            item => item.NodeId,
            item => item.Schema,
            StringComparer.OrdinalIgnoreCase);

    private static void AssertColumns(
        WorkflowTabularSchemaResult result,
        params (string Name, TabularDataType Type, bool Nullable)[] expected)
    {
        Assert.True(result.IsSuccess, $"{result.Code}: {result.Message}");
        Assert.Equal(expected.Length, result.Schema!.Columns.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Name, result.Schema.Columns[index].Name);
            Assert.Equal(expected[index].Type, result.Schema.Columns[index].DataType);
            Assert.Equal(expected[index].Nullable, result.Schema.Columns[index].IsNullable);
        }
    }
}
