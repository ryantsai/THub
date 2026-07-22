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
            [Node("source"), Node("target")],
            [new WorkflowEdge("source", "target")]);

        Assert.Empty(_validator.Validate(graph));
    }

    [Fact]
    public void CycleIsRejected()
    {
        var graph = new WorkflowGraph(
            [Node("one"), Node("two")],
            [new WorkflowEdge("one", "two"), new WorkflowEdge("two", "one")]);

        var issue = Assert.Single(_validator.Validate(graph));
        Assert.Equal("graph.cycle", issue.Code);
    }

    [Fact]
    public void MissingEdgeEndpointIsRejected()
    {
        var graph = new WorkflowGraph(
            [Node("source")],
            [new WorkflowEdge("source", "missing")]);

        Assert.Contains(_validator.Validate(graph), issue => issue.Code == "edge.target.missing");
    }

    private static WorkflowNode Node(string id) =>
        new(id, WorkflowNodeKind.SqlSource, id, 0, 0);
}

