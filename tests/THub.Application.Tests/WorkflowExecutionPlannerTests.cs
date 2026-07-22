using THub.Application.Execution;
using THub.Application.Workflows;
using THub.Domain.Workflows;

namespace THub.Application.Tests;

public sealed class WorkflowExecutionPlannerTests
{
    private readonly WorkflowExecutionPlanner _planner = new(new WorkflowGraphValidator());

    [Fact]
    public void ProducesDeterministicTopologicalLayersAndInputOrder()
    {
        var graph = new WorkflowGraph(
            [
                Node("source-z", WorkflowNodeKind.SqlSource),
                Node("source-a", WorkflowNodeKind.CsvSource),
                Node("join", WorkflowNodeKind.Join),
                Node("target", WorkflowNodeKind.SqlTarget)
            ],
            [
                new("source-z", "join"),
                new("source-a", "join"),
                new("join", "target")
            ]);

        var plan = _planner.CreatePlan(graph);

        Assert.Equal(3, plan.Layers.Count);
        Assert.Equal(["source-a", "source-z"], plan.Layers[0].Select(node => node.Node.Id));
        Assert.Equal(["source-a", "source-z"], plan.Layers[1][0].ParentNodeIds);
        Assert.Equal("target", plan.Layers[2][0].Node.Id);
    }

    [Fact]
    public void RejectsCyclesAtTheExecutionBoundary()
    {
        var graph = new WorkflowGraph(
            [
                Node("one", WorkflowNodeKind.SelectColumns),
                Node("two", WorkflowNodeKind.FilterRows)
            ],
            [new("one", "two"), new("two", "one")]);

        var exception = Assert.Throws<WorkflowPlanException>(() => _planner.CreatePlan(graph));

        Assert.Contains(exception.Issues, issue => issue.Code == "graph.cycle");
    }

    private static WorkflowNode Node(string id, WorkflowNodeKind kind) =>
        new(id, kind, id, 0, 0);
}
