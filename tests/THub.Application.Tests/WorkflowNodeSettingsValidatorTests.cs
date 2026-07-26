using THub.Application.Execution;
using THub.Domain.Workflows;

namespace THub.Application.Tests;

public sealed class WorkflowNodeSettingsValidatorTests
{
    private readonly WorkflowNodeSettingsValidator _validator = new();

    [Theory]
    [InlineData(WorkflowNodeKind.SqlSource, """{"connectionId":"11111111-1111-1111-1111-111111111111","schema":"dbo","object":"Orders","batchSize":1000}""")]
    [InlineData(WorkflowNodeKind.MySqlSource, """{"connectionId":"11111111-1111-1111-1111-111111111111","schema":"warehouse","object":"Orders","batchSize":1000}""")]
    [InlineData(WorkflowNodeKind.PostgreSqlSource, """{"connectionId":"11111111-1111-1111-1111-111111111111","schema":"public","object":"Orders","batchSize":1000}""")]
    [InlineData(WorkflowNodeKind.OracleSource, """{"connectionId":"11111111-1111-1111-1111-111111111111","schema":"APP","object":"ORDERS","batchSize":1000}""")]
    [InlineData(WorkflowNodeKind.FtpSource, """{"connectionId":"11111111-1111-1111-1111-111111111111","remotePath":"/inbound/orders.txt","format":"tabDelimited","hasHeader":true}""")]
    [InlineData(WorkflowNodeKind.CsvSource, """{"connectionId":"11111111-1111-1111-1111-111111111111","relativePath":"inbound/orders.csv","hasHeader":true,"delimiter":","}""")]
    [InlineData(WorkflowNodeKind.ExcelSource, """{"connectionId":"11111111-1111-1111-1111-111111111111","relativePath":"inbound/orders.xlsx","worksheet":"Orders","hasHeader":true}""")]
    [InlineData(WorkflowNodeKind.SelectColumns, """{"columns":["Id","Name"]}""")]
    [InlineData(WorkflowNodeKind.FilterRows, """{"conditions":[{"column":"Id","operator":"greaterThan","value":0}]}""")]
    [InlineData(WorkflowNodeKind.UnionRows, """{"inputNodeIds":["north","south"],"matchBy":"name","mode":"all"}""")]
    [InlineData(WorkflowNodeKind.DeriveColumns, """{"columns":[{"name":"Total","type":"Decimal","nullable":false,"expression":"row.Quantity * row.Price"}]}""")]
    [InlineData(WorkflowNodeKind.AggregateRows, """{"groupBy":["Region"],"aggregates":[{"name":"OrderCount","operation":"count"},{"name":"Revenue","operation":"sum","column":"Amount"}],"maximumGroups":100000}""")]
    [InlineData(WorkflowNodeKind.DistinctRows, """{"columns":["CustomerId"],"maximumKeys":100000}""")]
    [InlineData(WorkflowNodeKind.DistinctRows, """{"maximumKeys":100000}""")]
    [InlineData(WorkflowNodeKind.SortRows, """{"keys":[{"column":"CreatedAt","direction":"descending","nulls":"last"}],"maximumBufferedRows":100000}""")]
    [InlineData(WorkflowNodeKind.SqlTarget, """{"connectionId":"11111111-1111-1111-1111-111111111111","schema":"dbo","object":"Orders","mode":"insert","bindings":[{"targetColumn":"CreatedAtUtc","kind":"Variable","value":"runStartedAtUtc"}]}""")]
    [InlineData(WorkflowNodeKind.MySqlTarget, """{"connectionId":"11111111-1111-1111-1111-111111111111","schema":"warehouse","object":"Orders","mode":"insert"}""")]
    [InlineData(WorkflowNodeKind.PostgreSqlTarget, """{"connectionId":"11111111-1111-1111-1111-111111111111","schema":"public","object":"Orders","mode":"insert"}""")]
    [InlineData(WorkflowNodeKind.OracleTarget, """{"connectionId":"11111111-1111-1111-1111-111111111111","schema":"APP","object":"ORDERS","mode":"insert"}""")]
    [InlineData(WorkflowNodeKind.SqlTarget, """{"connectionId":"11111111-1111-1111-1111-111111111111","schema":"dbo","object":"Orders","mode":"upsert","keyColumns":["Id"],"bindings":[{"targetColumn":"Id","kind":"Column","value":"Id"},{"targetColumn":"Name","kind":"Column","value":"Name"}]}""")]
    [InlineData(WorkflowNodeKind.MySqlTarget, """{"connectionId":"11111111-1111-1111-1111-111111111111","schema":"warehouse","object":"Orders","mode":"upsert","keyColumns":["Id"]}""")]
    [InlineData(WorkflowNodeKind.PostgreSqlTarget, """{"connectionId":"11111111-1111-1111-1111-111111111111","schema":"public","object":"Orders","mode":"delete","keyColumns":["Id"]}""")]
    [InlineData(WorkflowNodeKind.OracleTarget, """{"connectionId":"11111111-1111-1111-1111-111111111111","schema":"APP","object":"ORDERS","mode":"delete","keyColumns":["ID"],"bindings":[{"targetColumn":"ID","kind":"Column","value":"Id"}]}""")]
    [InlineData(WorkflowNodeKind.FtpTarget, """{"connectionId":"11111111-1111-1111-1111-111111111111","remotePath":"/outbound/orders_{runStartedAtUtc:yyyyMMdd}.xlsx","format":"excel","worksheet":"Orders","includeHeader":true,"mode":"replace"}""")]
    [InlineData(WorkflowNodeKind.CsvTarget, """{"connectionId":"11111111-1111-1111-1111-111111111111","relativePath":"outbound/orders_{runStartedAtUtc:yyyyMMdd_HHmmss}.csv","includeHeader":true,"mode":"replace"}""")]
    [InlineData(WorkflowNodeKind.ExcelTarget, """{"connectionId":"11111111-1111-1111-1111-111111111111","relativePath":"outbound/orders_{runId}.xlsx","worksheet":"Orders","mode":"append"}""")]
    [InlineData(WorkflowNodeKind.EmailTarget, """{"profileId":"11111111-1111-1111-1111-111111111111","recipients":["owner@example.test"],"subject":"Data {{run.id}}","body":"<p>Results</p>{{data}}","deliveryMode":"inline","attachmentFileName":"results.csv"}""")]
    [InlineData(WorkflowNodeKind.EmailAlert, """{"profileId":"11111111-1111-1111-1111-111111111111","recipients":["ops@example.test"],"subject":"Run {{run.id}}","body":"Done"}""")]
    [InlineData(WorkflowNodeKind.Webhook, """{"trustedActionId":"11111111-1111-1111-1111-111111111111","body":"{}"}""")]
    [InlineData(WorkflowNodeKind.Executable, """{"trustedActionId":"11111111-1111-1111-1111-111111111111"}""")]
    public void ParseAcceptsStrictOperationalContract(WorkflowNodeKind kind, string settingsJson)
    {
        var parsed = _validator.Parse(new WorkflowNode("node", kind, "Node", 0, 0, settingsJson));

        Assert.NotNull(parsed);
    }

    [Fact]
    public void ParseCreatesCanonicalTransformContracts()
    {
        var union = Assert.IsType<UnionRowsNodeSettings>(_validator.Parse(Node(
            WorkflowNodeKind.UnionRows,
            """{"inputNodeIds":["north","south"],"matchBy":"name","mode":"all"}""")));
        Assert.Equal(["north", "south"], union.InputNodeIds);
        Assert.Equal(UnionMatchMode.Name, union.MatchBy);
        Assert.Equal(UnionRowMode.All, union.Mode);

        var derive = Assert.IsType<DeriveColumnsNodeSettings>(_validator.Parse(Node(
            WorkflowNodeKind.DeriveColumns,
            """{"columns":[{"name":"Total","type":"Decimal","nullable":false,"expression":"row.Quantity * row.Price"}]}""")));
        var derived = Assert.Single(derive.Columns);
        Assert.Equal("Total", derived.Name);
        Assert.Equal(TabularDataType.Decimal, derived.DataType);
        Assert.False(derived.IsNullable);
        Assert.Equal("row.Quantity * row.Price", derived.Expression);

        var aggregate = Assert.IsType<AggregateRowsNodeSettings>(_validator.Parse(Node(
            WorkflowNodeKind.AggregateRows,
            """{"groupBy":["Region"],"aggregates":[{"name":"OrderCount","operation":"count"},{"name":"Revenue","operation":"sum","column":"Amount"}],"maximumGroups":100000}""")));
        Assert.Equal(["Region"], aggregate.GroupBy);
        Assert.Collection(
            aggregate.Aggregates,
            item =>
            {
                Assert.Equal("OrderCount", item.Name);
                Assert.Equal(AggregateOperation.Count, item.Operation);
                Assert.Null(item.Column);
            },
            item =>
            {
                Assert.Equal("Revenue", item.Name);
                Assert.Equal(AggregateOperation.Sum, item.Operation);
                Assert.Equal("Amount", item.Column);
            });
        Assert.Equal(100_000, aggregate.MaximumGroups);

        var distinct = Assert.IsType<DistinctRowsNodeSettings>(_validator.Parse(Node(
            WorkflowNodeKind.DistinctRows,
            """{"columns":[],"maximumKeys":100000}""")));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(distinct.Columns));
        Assert.Equal(100_000, distinct.MaximumKeys);

        var sort = Assert.IsType<SortRowsNodeSettings>(_validator.Parse(Node(
            WorkflowNodeKind.SortRows,
            """{"keys":[{"column":"CreatedAt","direction":"descending","nulls":"last"}],"maximumBufferedRows":100000}""")));
        var key = Assert.Single(sort.Keys);
        Assert.Equal("CreatedAt", key.Column);
        Assert.Equal(SortDirection.Descending, key.Direction);
        Assert.Equal(SortNullPlacement.Last, key.Nulls);
        Assert.Equal(100_000, sort.MaximumBufferedRows);

        var fullJoin = Assert.IsType<JoinNodeSettings>(_validator.Parse(Node(
            WorkflowNodeKind.Join,
            """{"leftNodeId":"north","rightNodeId":"south","leftKeys":["Id"],"rightKeys":["Id"],"type":"full","maximumBufferedRows":100000}""")));
        Assert.Equal("full", fullJoin.JoinType);

        var rightJoin = Assert.IsType<JoinNodeSettings>(_validator.Parse(Node(
            WorkflowNodeKind.Join,
            """{"leftNodeId":"north","rightNodeId":"south","leftKeys":["Id"],"rightKeys":["Id"],"type":"right","maximumBufferedRows":100000}""")));
        Assert.Equal("right", rightJoin.JoinType);
    }

    [Theory]
    [InlineData(WorkflowNodeKind.UnionRows, """{"inputNodeIds":["north","NORTH"],"matchBy":"name","mode":"all"}""", "node.settings.array.duplicate")]
    [InlineData(WorkflowNodeKind.DeriveColumns, """{"columns":[{"name":"Total","type":"Decimal","nullable":false,"expression":"row.Amount"},{"name":"TOTAL","type":"Decimal","nullable":true,"expression":"row.Tax"}]}""", "node.derive.columns.duplicate")]
    [InlineData(WorkflowNodeKind.AggregateRows, """{"groupBy":[],"aggregates":[{"name":"Total","operation":"sum","column":"Amount"},{"name":"TOTAL","operation":"maximum","column":"Amount"}],"maximumGroups":100}""", "node.aggregate.outputs.duplicate")]
    [InlineData(WorkflowNodeKind.AggregateRows, """{"groupBy":[],"aggregates":[{"name":"Total","operation":"median","column":"Amount"}],"maximumGroups":100}""", "node.aggregate.operation.invalid")]
    [InlineData(WorkflowNodeKind.AggregateRows, """{"groupBy":[],"aggregates":[{"name":"Total","operation":"sum"}],"maximumGroups":100}""", "node.aggregate.column.required")]
    [InlineData(WorkflowNodeKind.AggregateRows, """{"groupBy":[],"aggregates":[{"name":"Count","operation":"count","column":"Amount"}],"maximumGroups":100}""", "node.aggregate.column.forbidden")]
    [InlineData(WorkflowNodeKind.DistinctRows, """{"columns":["CustomerId","CUSTOMERID"],"maximumKeys":100}""", "node.settings.array.duplicate")]
    [InlineData(WorkflowNodeKind.SortRows, """{"keys":[{"column":"CreatedAt","direction":"ascending","nulls":"first"},{"column":"CREATEDAT","direction":"descending","nulls":"last"}],"maximumBufferedRows":100}""", "node.sort.keys.duplicate")]
    [InlineData(WorkflowNodeKind.SortRows, """{"keys":[{"column":"CreatedAt","direction":"newest","nulls":"last"}],"maximumBufferedRows":100}""", "node.sort.direction.invalid")]
    [InlineData(WorkflowNodeKind.SortRows, """{"keys":[{"column":"CreatedAt","direction":"ascending","nulls":"middle"}],"maximumBufferedRows":100}""", "node.sort.nulls.invalid")]
    [InlineData(WorkflowNodeKind.Join, """{"leftNodeId":"north","rightNodeId":"south","leftKeys":["Id"],"rightKeys":["Id"],"type":"Right","maximumBufferedRows":100}""", "node.join.type.invalid")]
    [InlineData(WorkflowNodeKind.AggregateRows, """{"groupBy":[],"aggregates":[{"name":"Count","operation":"count"}],"maximumGroups":0}""", "node.settings.number.limit")]
    [InlineData(WorkflowNodeKind.DistinctRows, """{"maximumKeys":1000001}""", "node.settings.number.limit")]
    [InlineData(WorkflowNodeKind.SortRows, """{"keys":[{"column":"CreatedAt","direction":"ascending","nulls":"first"}],"maximumBufferedRows":0}""", "node.settings.number.limit")]
    public void ParseRejectsInvalidTransformSettings(
        WorkflowNodeKind kind,
        string settingsJson,
        string expectedCode)
    {
        var exception = Assert.Throws<WorkflowNodeSettingsException>(
            () => _validator.Parse(Node(kind, settingsJson)));

        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public void AggregateOutputNamesCannotDuplicateGroupByColumns()
    {
        var exception = Assert.Throws<WorkflowNodeSettingsException>(() => _validator.Parse(Node(
            WorkflowNodeKind.AggregateRows,
            """{"groupBy":["Region"],"aggregates":[{"name":"REGION","operation":"count"}],"maximumGroups":100}""")));

        Assert.Equal("node.aggregate.outputs.duplicate", exception.Code);
    }

    [Theory]
    [InlineData(
        WorkflowNodeKind.UnionRows,
        """{"inputNodeIds":["north","invalid/id"],"matchBy":"name","mode":"all"}""",
        "node.union.input.invalid")]
    [InlineData(
        WorkflowNodeKind.Join,
        """{"leftNodeId":"north","rightNodeId":"invalid/id","leftKeys":["Id"],"rightKeys":["Id"],"type":"inner","maximumBufferedRows":100}""",
        "node.join.input.invalid")]
    public void TransformInputNodeIdsUseKindSpecificInvalidCodes(
        WorkflowNodeKind kind,
        string settingsJson,
        string expectedCode)
    {
        var exception = Assert.Throws<WorkflowNodeSettingsException>(() => _validator.Parse(Node(
            kind,
            settingsJson)));

        Assert.Equal(expectedCode, exception.Code);
    }

    [Theory]
    [MemberData(nameof(UnknownTransformPropertyCases))]
    public void ParseRejectsUnknownTransformProperties(
        WorkflowNodeKind kind,
        string settingsJson)
    {
        var exception = Assert.Throws<WorkflowNodeSettingsException>(
            () => _validator.Parse(Node(kind, settingsJson)));

        Assert.Equal("node.settings.property.unsupported", exception.Code);
    }

    [Theory]
    [MemberData(nameof(DuplicateTransformPropertyCases))]
    public void ParseRejectsDuplicateTransformProperties(
        WorkflowNodeKind kind,
        string settingsJson)
    {
        var exception = Assert.Throws<WorkflowNodeSettingsException>(
            () => _validator.Parse(Node(kind, settingsJson)));

        Assert.Equal("node.settings.property.duplicate", exception.Code);
    }

    [Theory]
    [MemberData(nameof(TransformCollectionLimitCases))]
    public void ParseRejectsTransformCollectionCountsAboveTheirBounds(
        WorkflowNodeKind kind,
        string settingsJson,
        string expectedCode)
    {
        var exception = Assert.Throws<WorkflowNodeSettingsException>(
            () => _validator.Parse(Node(kind, settingsJson)));

        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public void ValidateRejectsInvalidDerivedColumnExpression()
    {
        var validator = new WorkflowNodeSettingsValidator(new RejectingExpressionSessionFactory());
        var graph = new WorkflowGraph(
            [
                new("source", WorkflowNodeKind.SqlSource, "Source", 0, 0, SqlSourceSettings()),
                Node(
                    WorkflowNodeKind.DeriveColumns,
                    """{"columns":[{"name":"Total","type":"Decimal","nullable":false,"expression":"row."}]}""")
            ],
            [new("source", "node")]);

        var issue = Assert.Single(validator.Validate(graph));

        Assert.Equal("node.derive.expression.invalid", issue.Code);
        Assert.Equal("node", issue.NodeId);
    }

    [Fact]
    public void ParseRejectsUnknownSettingsProperty()
    {
        var node = new WorkflowNode(
            "source",
            WorkflowNodeKind.SqlSource,
            "Source",
            0,
            0,
            """{"connectionId":"11111111-1111-1111-1111-111111111111","schema":"dbo","object":"Orders","batchSize":1000,"query":"SELECT *"}""");

        var exception = Assert.Throws<WorkflowNodeSettingsException>(() => _validator.Parse(node));

        Assert.Equal("node.settings.property.unsupported", exception.Code);
    }

    [Theory]
    [InlineData(
        """{"connectionId":"11111111-1111-1111-1111-111111111111","schema":"dbo","object":"Orders","mode":"upsert"}""",
        "node.sql-target.keys.required")]
    [InlineData(
        """{"connectionId":"11111111-1111-1111-1111-111111111111","schema":"dbo","object":"Orders","mode":"delete","keyColumns":["Id"],"bindings":[{"targetColumn":"Name","kind":"Column","value":"Name"}]}""",
        "node.sql-target.keys.binding")]
    [InlineData(
        """{"connectionId":"11111111-1111-1111-1111-111111111111","schema":"dbo","object":"Orders","mode":"delete","keyColumns":["Id"],"bindings":[{"targetColumn":"Id","kind":"Variable","value":"id"}]}""",
        "node.sql-target.keys.binding")]
    [InlineData(
        """{"connectionId":"11111111-1111-1111-1111-111111111111","schema":"dbo","object":"Orders","mode":"upsert","keyColumns":["Id"],"bindings":[{"targetColumn":"Id","kind":"Column","value":"Id"}]}""",
        "node.sql-target.upsert.values")]
    public void ParseRejectsUnsafeRelationalTargetMutationSettings(
        string settingsJson,
        string expectedCode)
    {
        var node = new WorkflowNode(
            "target",
            WorkflowNodeKind.SqlTarget,
            "Target",
            0,
            0,
            settingsJson);

        var exception = Assert.Throws<WorkflowNodeSettingsException>(() => _validator.Parse(node));

        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public void ValidateRequiresJoinInputIdsToMatchIncomingEdges()
    {
        var graph = new WorkflowGraph(
            [
                new("left", WorkflowNodeKind.SqlSource, "Left", 0, 0, SqlSourceSettings()),
                new("right", WorkflowNodeKind.SqlSource, "Right", 0, 0, SqlSourceSettings()),
                new(
                    "join",
                    WorkflowNodeKind.Join,
                    "Join",
                    0,
                    0,
                    """{"leftNodeId":"left","rightNodeId":"other","leftKeys":["Id"],"rightKeys":["Id"],"type":"inner","maximumBufferedRows":1000}""")
            ],
            [new("left", "join"), new("right", "join")]);

        var issue = Assert.Single(_validator.Validate(graph));

        Assert.Equal("node.join.inputs.mismatch", issue.Code);
        Assert.Equal("join", issue.NodeId);
    }

    [Fact]
    public void ValidateRequiresUnionInputIdsToMatchIncomingEdges()
    {
        var graph = new WorkflowGraph(
            [
                new("north", WorkflowNodeKind.SqlSource, "North", 0, 0, SqlSourceSettings()),
                new("south", WorkflowNodeKind.SqlSource, "South", 0, 0, SqlSourceSettings()),
                new(
                    "union",
                    WorkflowNodeKind.UnionRows,
                    "Union",
                    0,
                    0,
                    """{"inputNodeIds":["north","other"],"matchBy":"name","mode":"all"}""")
            ],
            [new("north", "union"), new("south", "union")]);

        var issue = Assert.Single(_validator.Validate(graph));

        Assert.Equal("node.union.inputs.mismatch", issue.Code);
        Assert.Equal("union", issue.NodeId);
    }

    [Fact]
    public void CsvWithoutHeaderRequiresTypedColumns()
    {
        var node = new WorkflowNode(
            "source",
            WorkflowNodeKind.CsvSource,
            "Source",
            0,
            0,
            """{"connectionId":"11111111-1111-1111-1111-111111111111","relativePath":"inbound/orders.csv","hasHeader":false}""");

        var exception = Assert.Throws<WorkflowNodeSettingsException>(() => _validator.Parse(node));

        Assert.Equal("node.csv.columns.required", exception.Code);
    }

    [Fact]
    public void ValidateRejectsUnknownVariableBinding()
    {
        var graph = new WorkflowGraph(
            [
                new(
                    "target",
                    WorkflowNodeKind.SqlTarget,
                    "Target",
                    0,
                    0,
                    """{"connectionId":"11111111-1111-1111-1111-111111111111","schema":"dbo","object":"Orders","mode":"insert","bindings":[{"targetColumn":"Tenant","kind":"Variable","value":"missing"}]}""")
            ],
            [],
            [],
            []);

        var issue = Assert.Single(_validator.Validate(graph));

        Assert.Equal("node.target.variable.missing", issue.Code);
    }

    [Fact]
    public void ValidateRejectsUnknownCsvFileNameVariable()
    {
        var graph = new WorkflowGraph(
            [
                new(
                    "target",
                    WorkflowNodeKind.CsvTarget,
                    "CSV target",
                    0,
                    0,
                    """{"connectionId":"11111111-1111-1111-1111-111111111111","relativePath":"export_{missing}.csv","includeHeader":true,"mode":"append"}""")
            ],
            [],
            [],
            []);

        var issue = Assert.Single(_validator.Validate(graph));

        Assert.Equal("node.file.path.variable.missing", issue.Code);
    }

    [Theory]
    [InlineData("append")]
    [InlineData("replace")]
    [InlineData("createNew")]
    public void CsvTargetAcceptsSupportedWriteModes(string mode)
    {
        var node = new WorkflowNode(
            "target",
            WorkflowNodeKind.CsvTarget,
            "CSV target",
            0,
            0,
            $$"""{"connectionId":"11111111-1111-1111-1111-111111111111","relativePath":"export.csv","includeHeader":true,"mode":"{{mode}}"}""");

        var settings = Assert.IsType<CsvTargetNodeSettings>(_validator.Parse(node));

        Assert.Equal(mode, settings.Mode);
    }

    [Theory]
    [InlineData("append")]
    [InlineData("replace")]
    [InlineData("createNew")]
    public void ExcelTargetAcceptsSupportedWriteModes(string mode)
    {
        var node = new WorkflowNode(
            "target",
            WorkflowNodeKind.ExcelTarget,
            "Excel target",
            0,
            0,
            $$"""{"connectionId":"11111111-1111-1111-1111-111111111111","relativePath":"export_{runId}.xlsx","worksheet":"Results","includeHeader":true,"mode":"{{mode}}"}""");

        var settings = Assert.IsType<ExcelTargetNodeSettings>(_validator.Parse(node));

        Assert.Equal(mode, settings.Mode);
    }

    [Theory]
    [InlineData("append")]
    [InlineData("replace")]
    [InlineData("createNew")]
    public void FtpTargetAcceptsSupportedWriteModes(string mode)
    {
        var node = new WorkflowNode(
            "target",
            WorkflowNodeKind.FtpTarget,
            "FTP target",
            0,
            0,
            $$"""{"connectionId":"11111111-1111-1111-1111-111111111111","remotePath":"/outbound/export_{runId}.csv","format":"csv","includeHeader":true,"mode":"{{mode}}"}""");

        var settings = Assert.IsType<FtpTargetNodeSettings>(_validator.Parse(node));

        Assert.Equal(mode, settings.Mode);
    }

    [Theory]
    [InlineData(
        WorkflowNodeKind.ExcelTarget,
        """{"connectionId":"11111111-1111-1111-1111-111111111111","relativePath":"{folder}/export.xlsx","worksheet":"Results","includeHeader":true,"mode":"append"}""")]
    [InlineData(
        WorkflowNodeKind.FtpTarget,
        """{"connectionId":"11111111-1111-1111-1111-111111111111","remotePath":"/{folder}/export.csv","format":"csv","includeHeader":true,"mode":"append"}""")]
    public void FileTargetRejectsVariablesInDirectorySegments(
        WorkflowNodeKind kind,
        string settingsJson)
    {
        var node = new WorkflowNode("target", kind, "Target", 0, 0, settingsJson);

        var exception = Assert.Throws<WorkflowNodeSettingsException>(() => _validator.Parse(node));

        Assert.Contains("path", exception.Code);
    }

    [Theory]
    [InlineData("relative/orders.csv")]
    [InlineData("/inbound/../orders.csv")]
    public void FtpPathMustBeAbsoluteAndTraversalFree(string remotePath)
    {
        var node = new WorkflowNode(
            "ftp",
            WorkflowNodeKind.FtpSource,
            "FTP",
            0,
            0,
            $$"""{"connectionId":"11111111-1111-1111-1111-111111111111","remotePath":"{{remotePath}}","format":"csv","hasHeader":true,"delimiter":","}""");

        var exception = Assert.Throws<WorkflowNodeSettingsException>(() => _validator.Parse(node));

        Assert.Equal("node.ftp.path.invalid", exception.Code);
    }

    [Fact]
    public void EmailBodyAllowsOrdinaryMultilineText()
    {
        var node = new WorkflowNode(
            "email",
            WorkflowNodeKind.EmailAlert,
            "Email",
            0,
            0,
            """{"profileId":"11111111-1111-1111-1111-111111111111","recipients":["ops@example.test"],"subject":"Run complete","body":"First line\nSecond line"}""");

        var settings = Assert.IsType<EmailAlertNodeSettings>(_validator.Parse(node));

        Assert.Contains(Environment.NewLine.Length == 2 ? "\n" : Environment.NewLine, settings.Body);
    }

    [Fact]
    public void InlineEmailTargetRequiresExactlyOneDataPlaceholder()
    {
        var node = new WorkflowNode(
            "email-target",
            WorkflowNodeKind.EmailTarget,
            "Email data",
            0,
            0,
            """{"profileId":"11111111-1111-1111-1111-111111111111","recipients":["ops@example.test"],"subject":"Data","body":"No data slot","deliveryMode":"inline","attachmentFileName":"results.csv"}""");

        var exception = Assert.Throws<WorkflowNodeSettingsException>(() => _validator.Parse(node));

        Assert.Equal("node.email-target.data-placeholder", exception.Code);
    }

    [Fact]
    public void AttachmentEmailTargetRequiresSafeCsvLeafName()
    {
        var node = new WorkflowNode(
            "email-target",
            WorkflowNodeKind.EmailTarget,
            "Email data",
            0,
            0,
            """{"profileId":"11111111-1111-1111-1111-111111111111","recipients":["ops@example.test"],"subject":"Data","body":"Attached","deliveryMode":"attachment","attachmentFileName":"../results.csv"}""");

        var exception = Assert.Throws<WorkflowNodeSettingsException>(() => _validator.Parse(node));

        Assert.Equal("node.email-target.attachment-name.invalid", exception.Code);
    }

    private static string SqlSourceSettings() =>
        """{"connectionId":"11111111-1111-1111-1111-111111111111","schema":"dbo","object":"Orders","batchSize":1000}""";

    private static WorkflowNode Node(WorkflowNodeKind kind, string settingsJson) =>
        new("node", kind, "Node", 0, 0, settingsJson);

    public static TheoryData<WorkflowNodeKind, string> UnknownTransformPropertyCases => new()
    {
        {
            WorkflowNodeKind.UnionRows,
            """{"inputNodeIds":["north","south"],"matchBy":"name","mode":"all","extra":true}"""
        },
        {
            WorkflowNodeKind.DistinctRows,
            """{"maximumKeys":100,"extra":true}"""
        },
        {
            WorkflowNodeKind.DeriveColumns,
            """{"columns":[{"name":"Total","type":"Decimal","nullable":false,"expression":"row.Amount","extra":true}]}"""
        },
        {
            WorkflowNodeKind.AggregateRows,
            """{"groupBy":[],"aggregates":[{"name":"Count","operation":"count","extra":true}],"maximumGroups":100}"""
        },
        {
            WorkflowNodeKind.SortRows,
            """{"keys":[{"column":"CreatedAt","direction":"ascending","nulls":"first","extra":true}],"maximumBufferedRows":100}"""
        }
    };

    public static TheoryData<WorkflowNodeKind, string> DuplicateTransformPropertyCases => new()
    {
        {
            WorkflowNodeKind.UnionRows,
            """{"inputNodeIds":["north","south"],"matchBy":"name","mode":"all","mode":"distinct"}"""
        },
        {
            WorkflowNodeKind.DeriveColumns,
            """{"columns":[{"name":"Total","name":"Other","type":"Decimal","nullable":false,"expression":"row.Amount"}]}"""
        },
        {
            WorkflowNodeKind.AggregateRows,
            """{"groupBy":[],"aggregates":[{"name":"Count","operation":"count","operation":"sum"}],"maximumGroups":100}"""
        },
        {
            WorkflowNodeKind.SortRows,
            """{"keys":[{"column":"CreatedAt","direction":"ascending","nulls":"first","nulls":"last"}],"maximumBufferedRows":100}"""
        }
    };

    public static TheoryData<WorkflowNodeKind, string, string> TransformCollectionLimitCases
    {
        get
        {
            var unionInputs = JsonStringArray("source", 17);
            var derivedColumns = JsonObjectArray(
                65,
                index => $$"""{"name":"Column{{index}}","type":"String","nullable":true,"expression":"row.Value"}""");
            var groupBy = JsonStringArray("Group", 17);
            var aggregates = JsonObjectArray(
                65,
                index => $$"""{"name":"Count{{index}}","operation":"count"}""");
            var distinctColumns = JsonStringArray("Column", 65);
            var sortKeys = JsonObjectArray(
                17,
                index => $$"""{"column":"Column{{index}}","direction":"ascending","nulls":"first"}""");

            return new()
            {
                {
                    WorkflowNodeKind.UnionRows,
                    $$"""{"inputNodeIds":{{unionInputs}},"matchBy":"name","mode":"all"}""",
                    "node.settings.array.limit"
                },
                {
                    WorkflowNodeKind.DeriveColumns,
                    $$"""{"columns":{{derivedColumns}}}""",
                    "node.derive.columns.limit"
                },
                {
                    WorkflowNodeKind.AggregateRows,
                    $$"""{"groupBy":{{groupBy}},"aggregates":[{"name":"Count","operation":"count"}],"maximumGroups":100}""",
                    "node.settings.array.limit"
                },
                {
                    WorkflowNodeKind.AggregateRows,
                    $$"""{"groupBy":[],"aggregates":{{aggregates}},"maximumGroups":100}""",
                    "node.aggregate.outputs.limit"
                },
                {
                    WorkflowNodeKind.DistinctRows,
                    $$"""{"columns":{{distinctColumns}},"maximumKeys":100}""",
                    "node.settings.array.limit"
                },
                {
                    WorkflowNodeKind.SortRows,
                    $$"""{"keys":{{sortKeys}},"maximumBufferedRows":100}""",
                    "node.sort.keys.limit"
                }
            };
        }
    }

    private static string JsonStringArray(string prefix, int count) =>
        "[" + string.Join(
            ",",
            Enumerable.Range(0, count).Select(index => $"\"{prefix}{index}\"")) + "]";

    private static string JsonObjectArray(int count, Func<int, string> createObject) =>
        "[" + string.Join(",", Enumerable.Range(0, count).Select(createObject)) + "]";

    private sealed class RejectingExpressionSessionFactory : IWorkflowExpressionSessionFactory
    {
        public IWorkflowExpressionSession Create(
            IReadOnlyList<WorkflowFunction> functions,
            IReadOnlyDictionary<string, TabularValue> variables,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Validate(IReadOnlyList<WorkflowFunction> functions)
        {
        }

        public void ValidateExpression(string expression) =>
            throw new InvalidOperationException("Invalid expression.");
    }
}
