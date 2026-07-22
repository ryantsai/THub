using THub.Domain.Workflows;

namespace THub.Application.Workflows;

public sealed class WorkflowGraphValidator
{
    public IReadOnlyList<GraphValidationIssue> Validate(WorkflowGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var issues = new List<GraphValidationIssue>();
        var nodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in graph.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
            {
                issues.Add(new("node.id.required", "Every node must have an id."));
            }
            else if (!nodeIds.Add(node.Id))
            {
                issues.Add(new("node.id.duplicate", $"Node id '{node.Id}' is duplicated.", node.Id));
            }

            if (string.IsNullOrWhiteSpace(node.Name))
            {
                issues.Add(new("node.name.required", "Every node must have a name.", node.Id));
            }
        }

        foreach (var edge in graph.Edges)
        {
            if (!nodeIds.Contains(edge.FromNodeId))
            {
                issues.Add(new("edge.source.missing", $"Edge source '{edge.FromNodeId}' does not exist."));
            }
            if (!nodeIds.Contains(edge.ToNodeId))
            {
                issues.Add(new("edge.target.missing", $"Edge target '{edge.ToNodeId}' does not exist."));
            }
            if (string.Equals(edge.FromNodeId, edge.ToNodeId, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new("edge.self", "A node cannot connect to itself.", edge.FromNodeId));
            }
        }

        if (issues.Count == 0 && ContainsCycle(graph, nodeIds))
        {
            issues.Add(new("graph.cycle", "Workflow graphs must be acyclic."));
        }

        return issues;
    }

    private static bool ContainsCycle(WorkflowGraph graph, HashSet<string> nodeIds)
    {
        var incoming = nodeIds.ToDictionary(id => id, _ => 0, StringComparer.OrdinalIgnoreCase);
        var outgoing = nodeIds.ToDictionary(id => id, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);

        foreach (var edge in graph.Edges)
        {
            incoming[edge.ToNodeId]++;
            outgoing[edge.FromNodeId].Add(edge.ToNodeId);
        }

        var ready = new Queue<string>(incoming.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var visited = 0;
        while (ready.TryDequeue(out var nodeId))
        {
            visited++;
            foreach (var target in outgoing[nodeId])
            {
                if (--incoming[target] == 0)
                {
                    ready.Enqueue(target);
                }
            }
        }

        return visited != nodeIds.Count;
    }
}

