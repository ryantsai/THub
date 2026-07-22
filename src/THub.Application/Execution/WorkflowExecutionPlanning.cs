using THub.Application.Workflows;
using THub.Domain.Workflows;

namespace THub.Application.Execution;

public sealed record WorkflowExecutionPlanNode(
    WorkflowNode Node,
    IReadOnlyList<string> ParentNodeIds,
    IReadOnlyList<string> ChildNodeIds);

public sealed class WorkflowExecutionPlan
{
    public WorkflowExecutionPlan(IReadOnlyList<IReadOnlyList<WorkflowExecutionPlanNode>> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        var materializedLayers = layers
            .Select(static layer =>
            {
                ArgumentNullException.ThrowIfNull(layer);
                return (IReadOnlyList<WorkflowExecutionPlanNode>)Array.AsReadOnly(layer.ToArray());
            })
            .ToArray();
        Layers = Array.AsReadOnly(materializedLayers);
        Nodes = Array.AsReadOnly(materializedLayers.SelectMany(static layer => layer).ToArray());
    }

    public IReadOnlyList<IReadOnlyList<WorkflowExecutionPlanNode>> Layers { get; }

    public IReadOnlyList<WorkflowExecutionPlanNode> Nodes { get; }
}

public sealed class WorkflowPlanException : Exception
{
    public WorkflowPlanException(IReadOnlyList<GraphValidationIssue> issues)
        : base("The workflow graph is not valid for execution.")
    {
        ArgumentNullException.ThrowIfNull(issues);
        Issues = Array.AsReadOnly(issues.ToArray());
    }

    public IReadOnlyList<GraphValidationIssue> Issues { get; }
}

public sealed class WorkflowExecutionPlanner
{
    private readonly WorkflowGraphValidator _validator;

    public WorkflowExecutionPlanner(WorkflowGraphValidator validator)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public WorkflowExecutionPlan CreatePlan(WorkflowGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var issues = _validator.Validate(graph);
        if (issues.Count > 0)
        {
            throw new WorkflowPlanException(issues);
        }

        var nodes = graph.Nodes.ToDictionary(
            static node => node.Id,
            StringComparer.OrdinalIgnoreCase);
        var parents = graph.Nodes.ToDictionary(
            static node => node.Id,
            static _ => new SortedSet<string>(StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);
        var children = graph.Nodes.ToDictionary(
            static node => node.Id,
            static _ => new SortedSet<string>(StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);
        var incomingCounts = graph.Nodes.ToDictionary(
            static node => node.Id,
            static _ => 0,
            StringComparer.OrdinalIgnoreCase);

        foreach (var edge in graph.Edges)
        {
            var sourceId = nodes[edge.FromNodeId].Id;
            var targetId = nodes[edge.ToNodeId].Id;
            parents[targetId].Add(sourceId);
            children[sourceId].Add(targetId);
            incomingCounts[targetId]++;
        }

        var ready = new SortedSet<string>(
            incomingCounts.Where(static pair => pair.Value == 0).Select(static pair => pair.Key),
            StringComparer.Ordinal);
        var layers = new List<IReadOnlyList<WorkflowExecutionPlanNode>>();
        var plannedCount = 0;

        while (ready.Count > 0)
        {
            var currentIds = ready.ToArray();
            var layer = currentIds
                .Select(nodeId => new WorkflowExecutionPlanNode(
                    nodes[nodeId],
                    parents[nodeId].ToArray(),
                    children[nodeId].ToArray()))
                .ToArray();
            layers.Add(layer);
            plannedCount += layer.Length;

            var next = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var nodeId in currentIds)
            {
                foreach (var childNodeId in children[nodeId])
                {
                    incomingCounts[childNodeId]--;
                    if (incomingCounts[childNodeId] == 0)
                    {
                        next.Add(childNodeId);
                    }
                }
            }

            ready = next;
        }

        if (plannedCount != graph.Nodes.Count)
        {
            throw new WorkflowPlanException(
                [new("graph.cycle", "Workflow graphs must be acyclic.")]);
        }

        return new WorkflowExecutionPlan(layers);
    }
}

public sealed class WorkflowNodeExecutorRegistry
{
    private readonly IReadOnlyDictionary<WorkflowNodeKind, IWorkflowNodeExecutor> _executors;

    public WorkflowNodeExecutorRegistry(IEnumerable<IWorkflowNodeExecutor> executors)
    {
        ArgumentNullException.ThrowIfNull(executors);
        var result = new Dictionary<WorkflowNodeKind, IWorkflowNodeExecutor>();
        foreach (var executor in executors)
        {
            ArgumentNullException.ThrowIfNull(executor);
            var kind = executor.Descriptor.NodeKind;
            if (!result.TryAdd(kind, executor))
            {
                throw new InvalidOperationException(
                    $"More than one workflow node executor is registered for '{kind}'.");
            }
        }

        _executors = result;
    }

    public bool TryGet(WorkflowNodeKind kind, out IWorkflowNodeExecutor? executor) =>
        _executors.TryGetValue(kind, out executor);
}
