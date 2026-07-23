namespace THub.Domain.Workflows;

public enum WorkflowVariableKind
{
    Literal,
    Database
}

public enum WorkflowValueType
{
    Boolean,
    Int64,
    Decimal,
    Double,
    String,
    DateTimeOffset,
    Guid
}

public sealed record WorkflowVariable(
    string Name,
    WorkflowVariableKind Kind,
    WorkflowValueType DataType,
    string? Value,
    Guid? ConnectionId = null,
    string? Schema = null,
    string? Object = null,
    string? ValueColumn = null,
    string? FilterColumn = null,
    string? FilterValue = null);

public sealed record WorkflowFunction(
    string Name,
    IReadOnlyList<string> Parameters,
    string Expression);

public sealed record WorkflowGraph(
    IReadOnlyList<WorkflowNode> Nodes,
    IReadOnlyList<WorkflowEdge> Edges,
    IReadOnlyList<WorkflowVariable> Variables,
    IReadOnlyList<WorkflowFunction> Functions)
{
    public WorkflowGraph(
        IReadOnlyList<WorkflowNode> nodes,
        IReadOnlyList<WorkflowEdge> edges)
        : this(nodes, edges, [], [])
    {
    }

    public static WorkflowGraph Empty { get; } = new([], [], [], []);
}
