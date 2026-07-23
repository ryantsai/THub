using System.Text.Json;
using THub.Application.Execution;
using THub.Domain.Workflows;

namespace THub.Application.Workflows;

public sealed class WorkflowGraphValidator
{
    public const int MaximumNodes = 200;
    public const int MaximumEdges = 1_000;
    public const int MaximumNodeSettingsCharacters = 100_000;
    public const int MaximumVariables = 64;
    public const int MaximumFunctions = 32;
    public const int MaximumVariableValueCharacters = 4_096;
    public const int MaximumFunctionExpressionCharacters = 4_096;
    public const int MaximumFunctionParameters = 8;
    private readonly IWorkflowExpressionSessionFactory? expressionSessionFactory;

    public WorkflowGraphValidator(
        IWorkflowExpressionSessionFactory? expressionSessionFactory = null)
    {
        this.expressionSessionFactory = expressionSessionFactory;
    }

    public IReadOnlyList<GraphValidationIssue> Validate(WorkflowGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var issues = new List<GraphValidationIssue>();
        if (graph.Nodes is null || graph.Edges is null
            || graph.Variables is null || graph.Functions is null)
        {
            return [new(
                "graph.collections.required",
                "Workflow variables, functions, nodes, and edges are required.")];
        }

        ValidateVariables(graph.Variables, issues);
        ValidateFunctions(graph.Functions, issues);

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

    private static void ValidateVariables(
        IReadOnlyList<WorkflowVariable> variables,
        ICollection<GraphValidationIssue> issues)
    {
        if (variables.Count > MaximumVariables)
        {
            issues.Add(new(
                "graph.variables.limit",
                $"A workflow cannot define more than {MaximumVariables} variables."));
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in variables)
        {
            if (!IsValidSymbol(variable.Name))
            {
                issues.Add(new(
                    "variable.name.invalid",
                    "Variable names must be 1-64 character JavaScript identifiers."));
                continue;
            }

            if (!names.Add(variable.Name)
                || variable.Name is "row" or "vars" or "run" or "time")
            {
                issues.Add(new(
                    "variable.name.duplicate",
                    $"Variable name '{variable.Name}' is duplicated or reserved."));
            }

            if (!Enum.IsDefined(variable.Kind) || !Enum.IsDefined(variable.DataType))
            {
                issues.Add(new(
                    "variable.type.invalid",
                    $"Variable '{variable.Name}' has an unsupported kind or data type."));
                continue;
            }

            if (variable.Kind == WorkflowVariableKind.Literal)
            {
                if (variable.Value is null
                    || variable.Value.Length > MaximumVariableValueCharacters)
                {
                    issues.Add(new(
                        "variable.literal.invalid",
                        $"Literal variable '{variable.Name}' requires a value no longer than {MaximumVariableValueCharacters} characters."));
                }
                continue;
            }

            if (variable.ConnectionId is null || variable.ConnectionId == Guid.Empty
                || !IsBoundedIdentifier(variable.Schema)
                || !IsBoundedIdentifier(variable.Object)
                || !IsBoundedIdentifier(variable.ValueColumn)
                || !IsBoundedIdentifier(variable.FilterColumn)
                || variable.FilterValue is null
                || variable.FilterValue.Length > MaximumVariableValueCharacters)
            {
                issues.Add(new(
                    "variable.database.invalid",
                    $"Database variable '{variable.Name}' requires an approved connection, object, value column, filter column, and bounded filter value."));
            }
        }
    }

    private void ValidateFunctions(
        IReadOnlyList<WorkflowFunction> functions,
        ICollection<GraphValidationIssue> issues)
    {
        if (functions.Count > MaximumFunctions)
        {
            issues.Add(new(
                "graph.functions.limit",
                $"A workflow cannot define more than {MaximumFunctions} functions."));
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var function in functions)
        {
            if (!IsValidSymbol(function.Name)
                || !names.Add(function.Name)
                || function.Name is "row" or "vars" or "run" or "time")
            {
                issues.Add(new(
                    "function.name.invalid",
                    $"Function name '{function.Name}' must be a unique, non-reserved JavaScript identifier."));
            }

            if (function.Parameters.Count > MaximumFunctionParameters
                || function.Parameters.Any(parameter => !IsValidSymbol(parameter))
                || function.Parameters.Distinct(StringComparer.Ordinal).Count()
                    != function.Parameters.Count)
            {
                issues.Add(new(
                    "function.parameters.invalid",
                    $"Function '{function.Name}' has invalid or duplicate parameters."));
            }

            if (string.IsNullOrWhiteSpace(function.Expression)
                || function.Expression.Length > MaximumFunctionExpressionCharacters)
            {
                issues.Add(new(
                    "function.expression.invalid",
                    $"Function '{function.Name}' requires an expression no longer than {MaximumFunctionExpressionCharacters} characters."));
            }
        }

        if (expressionSessionFactory is not null
            && !issues.Any(issue => issue.Code.StartsWith(
                "function.",
                StringComparison.Ordinal)))
        {
            try
            {
                expressionSessionFactory.Validate(functions);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                issues.Add(new(
                    "function.javascript.invalid",
                    "A workflow JavaScript function contains an invalid or unsupported expression."));
            }
        }
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
        WorkflowNodeKind.SqlSource or WorkflowNodeKind.MySqlSource
            or WorkflowNodeKind.PostgreSqlSource or WorkflowNodeKind.OracleSource
            or WorkflowNodeKind.FtpSource or WorkflowNodeKind.CsvSource
            or WorkflowNodeKind.ExcelSource;

    private static bool IsValidNodeId(string id)
    {
        if (id.Length is < 1 or > 128 || !char.IsLetterOrDigit(id[0]))
        {
            return false;
        }

        return id.All(character => char.IsLetterOrDigit(character)
            || character is '.' or '_' or '-');
    }

    private static bool IsValidSymbol(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 64
        && (char.IsLetter(value[0]) || value[0] is '_' or '$')
        && value.Skip(1).All(character =>
            char.IsLetterOrDigit(character) || character is '_' or '$');

    private static bool IsBoundedIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(character => !char.IsControl(character));

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
