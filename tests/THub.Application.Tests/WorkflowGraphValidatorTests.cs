using THub.Application.Workflows;
using THub.Domain.Workflows;

namespace THub.Application.Tests;

public sealed class WorkflowGraphValidatorTests
{
    private readonly WorkflowGraphValidator _validator = new();

    [Fact]
    public void ValidDirectedAcyclicGraphHasNoIssues()
    {
        var graph = new WorkflowGraph(
            [Node("source", WorkflowNodeKind.SqlSource), Node("target", WorkflowNodeKind.SqlTarget)],
            [new WorkflowEdge("source", "target")]);

        Assert.Empty(_validator.Validate(graph));
    }

    [Fact]
    public void CycleIsRejected()
    {
        var graph = new WorkflowGraph(
            [Node("one", WorkflowNodeKind.SelectColumns), Node("two", WorkflowNodeKind.FilterRows)],
            [new WorkflowEdge("one", "two"), new WorkflowEdge("two", "one")]);

        Assert.Contains(_validator.Validate(graph), issue => issue.Code == "graph.cycle");
    }

    [Fact]
    public void MissingEdgeEndpointIsRejected()
    {
        var graph = new WorkflowGraph(
            [Node("source", WorkflowNodeKind.SqlSource)],
            [new WorkflowEdge("source", "missing")]);

        Assert.Contains(_validator.Validate(graph), issue => issue.Code == "edge.target.missing");
    }

    [Fact]
    public void EmptyGraphIsRejected()
    {
        Assert.Contains(
            _validator.Validate(WorkflowGraph.Empty),
            issue => issue.Code == "graph.empty");
    }

    [Fact]
    public void DuplicateEdgeIsRejected()
    {
        var graph = new WorkflowGraph(
            [Node("source", WorkflowNodeKind.SqlSource), Node("target", WorkflowNodeKind.SqlTarget)],
            [new("source", "target"), new("SOURCE", "TARGET")]);

        Assert.Contains(_validator.Validate(graph), issue => issue.Code == "edge.duplicate");
    }

    [Fact]
    public void JoinRequiresExactlyTwoInputs()
    {
        var graph = new WorkflowGraph(
            [Node("source", WorkflowNodeKind.SqlSource), Node("join", WorkflowNodeKind.Join)],
            [new("source", "join")]);

        Assert.Contains(
            _validator.Validate(graph),
            issue => issue.Code == "node.input.cardinality" && issue.NodeId == "join");
    }

    [Fact]
    public void InvalidSettingsAreRejected()
    {
        var graph = new WorkflowGraph(
            [new("source", WorkflowNodeKind.SqlSource, "Source", 0, 0, "not-json")],
            []);

        Assert.Contains(_validator.Validate(graph), issue => issue.Code == "node.settings.invalid");
    }

    [Fact]
    public void VariablesAndFunctionsRequireUniqueSafeSymbols()
    {
        var graph = new WorkflowGraph(
            [Node("source", WorkflowNodeKind.SqlSource)],
            [],
            [
                new("region", WorkflowVariableKind.Literal, WorkflowValueType.String, "north"),
                new("REGION", WorkflowVariableKind.Literal, WorkflowValueType.String, "south")
            ],
            [new("row", ["value", "value"], "value")]);

        var issues = _validator.Validate(graph);

        Assert.Contains(issues, issue => issue.Code == "variable.name.duplicate");
        Assert.Contains(issues, issue => issue.Code == "function.name.invalid");
        Assert.Contains(issues, issue => issue.Code == "function.parameters.invalid");
    }

    [Theory]
    [InlineData(WorkflowNodeKind.Webhook)]
    [InlineData(WorkflowNodeKind.Executable)]
    public void TrustedActionKindsAreStructurallySupported(WorkflowNodeKind kind)
    {
        var graph = new WorkflowGraph(
            [Node("source", WorkflowNodeKind.SqlSource), Node("action", kind)],
            [new("source", "action")]);

        Assert.DoesNotContain(
            _validator.Validate(graph),
            issue => issue.Code == "node.kind.disabled");
    }

    private static WorkflowNode Node(string id, WorkflowNodeKind kind) =>
        new(id, kind, id, 0, 0);
}
