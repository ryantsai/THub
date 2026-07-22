namespace THub.Domain.Workflows;

public sealed record WorkflowGraph(
    IReadOnlyList<WorkflowNode> Nodes,
    IReadOnlyList<WorkflowEdge> Edges)
{
    public static WorkflowGraph Empty { get; } = new([], []);
}

