using System.Text.Json;
using THub.Domain.Workflows;

namespace THub.Application.Workflows;

public sealed class WorkflowGraphValidator
{
    public const int MaximumNodes = 200;
    public const int MaximumEdges = 1_000;
    public const int MaximumNodeSettingsCharacters = 100_000;

    public IReadOnlyList<GraphValidationIssue> Validate(WorkflowGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var issues = new List<GraphValidationIssue>();
        if (graph.Nodes is null || graph.Edges is null)
        {
            return [new("graph.collections.required", "Workflow nodes and edges are required.")];
        }

        if (graph.Nodes.Count == 0)
        {
            issues.Add(new("graph.empty", "A workflow must contain at least one node."));
        }
        if (graph.Nodes.Count > MaximumNodes)
        {
            issues.Add(new(
                "graph.nodes.limit",
                $"A workflow cannot contain more than {MaximumNodes} nodes."));
        }
        if (graph.Edges.Count > MaximumEdges)
        {
            issues.Add(new(
                "graph.edges.limit",
                $"A workflow cannot contain more than {MaximumEdges} edges."));
        }

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
            else if (!IsValidNodeId(node.Id))
            {
                issues.Add(new(
                    "node.id.invalid",
                    $"Node id '{node.Id}' must be 1-128 characters using letters, numbers, '.', '_' or '-'.",
                    node.Id));
            }

            if (string.IsNullOrWhiteSpace(node.Name))
            {
                issues.Add(new("node.name.required", "Every node must have a name.", node.Id));
            }
            else if (node.Name.Length > 200)
            {
                issues.Add(new(
                    "node.name.limit",
                    "A node name cannot exceed 200 characters.",
                    node.Id));
            }

            if (!double.IsFinite(node.X) || !double.IsFinite(node.Y)
                || Math.Abs(node.X) > 1_000_000 || Math.Abs(node.Y) > 1_000_000)
            {
                issues.Add(new(
                    "node.position.invalid",
                    "Node coordinates must be finite values within the supported canvas range.",
                    node.Id));
            }

            if (!Enum.IsDefined(node.Kind))
            {
                issues.Add(new(
                    "node.kind.unsupported",
                    $"Node kind '{node.Kind}' is not supported.",
                    node.Id));
            }

            ValidateSettings(node, issues);
            ValidateOperationalPolicy(node, issues);
        }

        var edgeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var structurallyValidEdges = new List<WorkflowEdge>();
        foreach (var edge in graph.Edges)
        {
            var edgeIsValid = true;
            if (!nodeIds.Contains(edge.FromNodeId))
            {
                issues.Add(new("edge.source.missing", $"Edge source '{edge.FromNodeId}' does not exist."));
                edgeIsValid = false;
            }
            if (!nodeIds.Contains(edge.ToNodeId))
            {
                issues.Add(new("edge.target.missing", $"Edge target '{edge.ToNodeId}' does not exist."));
                edgeIsValid = false;
            }
            if (string.Equals(edge.FromNodeId, edge.ToNodeId, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new("edge.self", "A node cannot connect to itself.", edge.FromNodeId));
                edgeIsValid = false;
            }

            if (!edgeKeys.Add($"{edge.FromNodeId}\u001f{edge.ToNodeId}"))
            {
                issues.Add(new(
                    "edge.duplicate",
                    $"Edge '{edge.FromNodeId}' to '{edge.ToNodeId}' is duplicated."));
                edgeIsValid = false;
            }

            if (edgeIsValid)
            {
                structurallyValidEdges.Add(edge);
            }
        }

        if (nodeIds.Count == graph.Nodes.Count
            && structurallyValidEdges.Count == graph.Edges.Count)
        {
            if (ContainsCycle(structurallyValidEdges, nodeIds))
            {
                issues.Add(new("graph.cycle", "Workflow graphs must be acyclic."));
            }
            else
            {
                ValidateCardinality(graph.Nodes, structurallyValidEdges, issues);
            }
        }

        return issues;
    }

    private static void ValidateSettings(
        WorkflowNode node,
        ICollection<GraphValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(node.SettingsJson))
        {
            issues.Add(new(
                "node.settings.required",
                "Node settings must be a JSON object.",
                node.Id));
            return;
        }

        if (node.SettingsJson.Length > MaximumNodeSettingsCharacters)
        {
            issues.Add(new(
                "node.settings.limit",
                $"Node settings cannot exceed {MaximumNodeSettingsCharacters} characters.",
                node.Id));
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(node.SettingsJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new(
                    "node.settings.object",
                    "Node settings must be a JSON object.",
                    node.Id));
            }
        }
        catch (JsonException)
        {
            issues.Add(new(
                "node.settings.invalid",
                "Node settings contain invalid JSON.",
                node.Id));
        }
    }

    private static void ValidateOperationalPolicy(
        WorkflowNode node,
        ICollection<GraphValidationIssue> issues)
    {
        if (node.Kind is WorkflowNodeKind.Webhook or WorkflowNodeKind.Executable)
        {
            issues.Add(new(
                "node.kind.disabled",
                $"{node.Kind} execution is disabled until an administrator policy authorizes it.",
                node.Id));
        }

        if (node.Kind is WorkflowNodeKind.PublishRestApi or WorkflowNodeKind.PublishDataEditor)
        {
            issues.Add(new(
                "node.publication.separate",
                "Data publications are governed resources and cannot execute as ordinary workflow nodes.",
                node.Id));
        }
    }

    private static void ValidateCardinality(
        IReadOnlyList<WorkflowNode> nodes,
        IReadOnlyList<WorkflowEdge> edges,
        ICollection<GraphValidationIssue> issues)
    {
        var incoming = nodes.ToDictionary(node => node.Id, _ => 0, StringComparer.OrdinalIgnoreCase);
        var outgoing = nodes.ToDictionary(node => node.Id, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var edge in edges)
        {
            incoming[edge.ToNodeId]++;
            outgoing[edge.FromNodeId]++;
        }

        foreach (var node in nodes)
        {
            if (IsSource(node.Kind))
            {
                if (incoming[node.Id] != 0)
                {
                    issues.Add(new(
                        "node.input.source",
                        "Source nodes cannot have incoming edges.",
                        node.Id));
                }
                if (outgoing[node.Id] == 0)
                {
                    issues.Add(new(
                        "node.output.required",
                        "A source node must feed at least one downstream node.",
                        node.Id));
                }
                continue;
            }

            var requiredInputs = node.Kind == WorkflowNodeKind.Join ? 2 : 1;
            if (incoming[node.Id] != requiredInputs)
            {
                issues.Add(new(
                    "node.input.cardinality",
                    $"{node.Kind} requires exactly {requiredInputs} incoming " +
                    (requiredInputs == 1 ? "edge." : "edges."),
                    node.Id));
            }
        }
    }

    private static bool IsSource(WorkflowNodeKind kind) => kind is
        WorkflowNodeKind.SqlSource or WorkflowNodeKind.CsvSource or WorkflowNodeKind.ExcelSource;

    private static bool IsValidNodeId(string id)
    {
        if (id.Length is < 1 or > 128 || !char.IsLetterOrDigit(id[0]))
        {
            return false;
        }

        return id.All(character => char.IsLetterOrDigit(character)
            || character is '.' or '_' or '-');
    }

    private static bool ContainsCycle(
        IReadOnlyList<WorkflowEdge> edges,
        HashSet<string> nodeIds)
    {
        var incoming = nodeIds.ToDictionary(id => id, _ => 0, StringComparer.OrdinalIgnoreCase);
        var outgoing = nodeIds.ToDictionary(id => id, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);

        foreach (var edge in edges)
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
