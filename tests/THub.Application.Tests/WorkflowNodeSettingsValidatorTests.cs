using THub.Application.Execution;
using THub.Domain.Workflows;

namespace THub.Application.Tests;

public sealed class WorkflowNodeSettingsValidatorTests
{
    private readonly WorkflowNodeSettingsValidator _validator = new();

    [Theory]
    [InlineData(WorkflowNodeKind.SqlSource, """{"connectionId":"11111111-1111-1111-1111-111111111111","schema":"dbo","object":"Orders","batchSize":1000}""")]
    [InlineData(WorkflowNodeKind.CsvSource, """{"connectionId":"11111111-1111-1111-1111-111111111111","relativePath":"inbound/orders.csv","hasHeader":true,"delimiter":","}""")]
    [InlineData(WorkflowNodeKind.ExcelSource, """{"connectionId":"11111111-1111-1111-1111-111111111111","relativePath":"inbound/orders.xlsx","worksheet":"Orders","hasHeader":true}""")]
    [InlineData(WorkflowNodeKind.SelectColumns, """{"columns":["Id","Name"]}""")]
    [InlineData(WorkflowNodeKind.FilterRows, """{"conditions":[{"column":"Id","operator":"greaterThan","value":0}]}""")]
    [InlineData(WorkflowNodeKind.SqlTarget, """{"connectionId":"11111111-1111-1111-1111-111111111111","schema":"dbo","object":"Orders","mode":"insert"}""")]
    [InlineData(WorkflowNodeKind.CsvTarget, """{"connectionId":"11111111-1111-1111-1111-111111111111","relativePath":"outbound/orders.csv","includeHeader":true}""")]
    [InlineData(WorkflowNodeKind.ExcelTarget, """{"connectionId":"11111111-1111-1111-1111-111111111111","relativePath":"outbound/orders.xlsx","worksheet":"Orders"}""")]
    [InlineData(WorkflowNodeKind.EmailAlert, """{"profileId":"11111111-1111-1111-1111-111111111111","recipients":["ops@example.test"],"subject":"Run {{run.id}}","body":"Done"}""")]
    public void ParseAcceptsStrictOperationalContract(WorkflowNodeKind kind, string settingsJson)
    {
        var parsed = _validator.Parse(new WorkflowNode("node", kind, "Node", 0, 0, settingsJson));

        Assert.NotNull(parsed);
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

    private static string SqlSourceSettings() =>
        """{"connectionId":"11111111-1111-1111-1111-111111111111","schema":"dbo","object":"Orders","batchSize":1000}""";
}
