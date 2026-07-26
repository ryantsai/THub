using THub.Application.Execution;
using THub.Domain.Workflows;

namespace THub.Application.Workflows;

public sealed record WorkflowTabularSchemaResult(
    TabularSchema? Schema,
    string? Code,
    string? Message)
{
    public bool IsSuccess => Schema is not null;

    internal static WorkflowTabularSchemaResult Success(TabularSchema schema) =>
        new(schema, null, null);

    internal static WorkflowTabularSchemaResult Failure(string code, string message) =>
        new(null, code, message);
}

/// <summary>
/// Resolves the tabular shape of a workflow node from already-inspected source metadata.
/// The service is pure: it does not inspect connections or read row values.
/// </summary>
public sealed class WorkflowTabularSchemaService(
    WorkflowNodeSettingsValidator settingsValidator)
{
    private readonly WorkflowNodeSettingsValidator settingsValidator =
        settingsValidator ?? throw new ArgumentNullException(nameof(settingsValidator));

    public WorkflowTabularSchemaResult Resolve(
        WorkflowGraph graph,
        string nodeId,
        IReadOnlyDictionary<string, TabularSchema> knownSourceSchemas)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(knownSourceSchemas);

        if (!TryCreateResolutionContext(
                graph,
                knownSourceSchemas,
                out var context,
                out var failure))
        {
            return failure!;
        }

        return ResolveNode(nodeId, context!);
    }

    public IReadOnlyList<WorkflowTabularSchemaResult> Resolve(
        WorkflowGraph graph,
        IReadOnlyList<string> nodeIds,
        IReadOnlyDictionary<string, TabularSchema> knownSourceSchemas)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(nodeIds);
        ArgumentNullException.ThrowIfNull(knownSourceSchemas);
        foreach (var nodeId in nodeIds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        }

        if (!TryCreateResolutionContext(
                graph,
                knownSourceSchemas,
                out var context,
                out var failure))
        {
            return nodeIds.Select(_ => failure!).ToArray();
        }

        return nodeIds
            .Select(nodeId => ResolveNode(nodeId, context!))
            .ToArray();
    }

    private static bool TryCreateResolutionContext(
        WorkflowGraph graph,
        IReadOnlyDictionary<string, TabularSchema> knownSourceSchemas,
        out ResolutionContext? context,
        out WorkflowTabularSchemaResult? failure)
    {
        var nodes = new Dictionary<string, WorkflowNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in graph.Nodes)
        {
            if (!nodes.TryAdd(node.Id, node))
            {
                context = null;
                failure = WorkflowTabularSchemaResult.Failure(
                    "schema.graph.node.duplicate",
                    $"Workflow node id '{node.Id}' is duplicated.");
                return false;
            }
        }

        var sources = new Dictionary<string, TabularSchema>(StringComparer.OrdinalIgnoreCase);
        foreach (var (sourceNodeId, schema) in knownSourceSchemas)
        {
            if (schema is null)
            {
                context = null;
                failure = WorkflowTabularSchemaResult.Failure(
                    "schema.source.invalid",
                    $"Known source schema '{sourceNodeId}' is null.");
                return false;
            }
            if (!sources.TryAdd(sourceNodeId, schema))
            {
                context = null;
                failure = WorkflowTabularSchemaResult.Failure(
                    "schema.source.duplicate",
                    $"Known source schema id '{sourceNodeId}' is duplicated.");
                return false;
            }
        }

        context = new ResolutionContext(graph, nodes, sources);
        failure = null;
        return true;
    }

    private WorkflowTabularSchemaResult ResolveNode(
        string nodeId,
        ResolutionContext context)
    {
        if (context.Results.TryGetValue(nodeId, out var cached))
        {
            return cached;
        }

        if (!context.Nodes.TryGetValue(nodeId, out var node))
        {
            return WorkflowTabularSchemaResult.Failure(
                "schema.node.unresolved",
                $"Workflow node '{nodeId}' does not exist.");
        }

        if (!context.Visiting.Add(node.Id))
        {
            var cycleStart = context.Path.FindIndex(
                item => string.Equals(item, node.Id, StringComparison.OrdinalIgnoreCase));
            var cycle = context.Path
                .Skip(Math.Max(0, cycleStart))
                .Append(node.Id);
            return WorkflowTabularSchemaResult.Failure(
                "schema.graph.cycle",
                $"Schema resolution encountered a workflow cycle: {string.Join(" -> ", cycle)}.");
        }

        context.Path.Add(node.Id);
        WorkflowTabularSchemaResult result;
        try
        {
            result = ResolveNodeCore(node, context);
        }
        catch (WorkflowNodeSettingsException exception)
        {
            result = WorkflowTabularSchemaResult.Failure(
                "schema.settings.invalid",
                $"Node '{node.Id}' settings are invalid ({exception.Code}): {exception.Message}");
        }
        catch (SchemaResolutionException exception)
        {
            result = WorkflowTabularSchemaResult.Failure(exception.Code, exception.Message);
        }
        catch (WorkflowTransformSchemaException exception)
        {
            result = WorkflowTabularSchemaResult.Failure(exception.Code, exception.Message);
        }
        catch (ArgumentException exception)
        {
            result = WorkflowTabularSchemaResult.Failure(
                "schema.output.invalid",
                $"Node '{node.Id}' cannot produce a valid tabular schema: {exception.Message}");
        }
        finally
        {
            context.Path.RemoveAt(context.Path.Count - 1);
            context.Visiting.Remove(node.Id);
        }

        context.Results[node.Id] = result;
        return result;
    }

    private WorkflowTabularSchemaResult ResolveNodeCore(
        WorkflowNode node,
        ResolutionContext context)
    {
        if (IsSource(node.Kind))
        {
            if (!context.SourceSchemas.TryGetValue(node.Id, out var sourceSchema))
            {
                return WorkflowTabularSchemaResult.Failure(
                    "schema.source.unresolved",
                    $"Schema metadata has not been loaded for source node '{node.Id}'.");
            }

            var sourceSettings = settingsValidator.Parse(node);
            if (sourceSettings is not SqlSourceNodeSettings relationalSettings)
            {
                return WorkflowTabularSchemaResult.Success(sourceSchema);
            }

            return WorkflowTabularSchemaResult.Success(
                relationalSettings.Columns is null
                    ? sourceSchema
                    : new TabularSchema(
                        relationalSettings.Columns.Select(
                            column => FindColumn(sourceSchema, column))));
        }

        return node.Kind switch
        {
            WorkflowNodeKind.SelectColumns => ResolveSingleInput(
                node,
                context,
                static (schema, settings) => Select(
                    schema,
                    (SelectColumnsNodeSettings)settings)),
            WorkflowNodeKind.FilterRows => ResolveSingleInput(
                node,
                context,
                static (schema, settings) => Filter(
                    schema,
                    (FilterRowsNodeSettings)settings)),
            WorkflowNodeKind.DeriveColumns => ResolveSingleInput(
                node,
                context,
                static (schema, settings) => Derive(
                    schema,
                    (DeriveColumnsNodeSettings)settings)),
            WorkflowNodeKind.AggregateRows => ResolveSingleInput(
                node,
                context,
                static (schema, settings) =>
                    WorkflowTransformSchemaSemantics.CreateAggregateSchema(
                        schema,
                        (AggregateRowsNodeSettings)settings)),
            WorkflowNodeKind.DistinctRows => ResolveSingleInput(
                node,
                context,
                static (schema, settings) => Distinct(
                    schema,
                    (DistinctRowsNodeSettings)settings)),
            WorkflowNodeKind.SortRows => ResolveSingleInput(
                node,
                context,
                static (schema, settings) => Sort(
                    schema,
                    (SortRowsNodeSettings)settings)),
            WorkflowNodeKind.Join => ResolveJoin(node, context),
            WorkflowNodeKind.UnionRows => ResolveUnion(node, context),
            _ => WorkflowTabularSchemaResult.Failure(
                "schema.node.unsupported",
                $"Node '{node.Id}' of kind '{node.Kind}' does not produce a designer tabular schema.")
        };
    }

    private WorkflowTabularSchemaResult ResolveSingleInput(
        WorkflowNode node,
        ResolutionContext context,
        Func<TabularSchema, WorkflowNodeSettings, TabularSchema> transform)
    {
        var incoming = IncomingNodeIds(node.Id, context.Graph);
        if (incoming.Count != 1)
        {
            return WorkflowTabularSchemaResult.Failure(
                "schema.input.cardinality",
                $"Node '{node.Id}' requires exactly one schema-producing input; found {incoming.Count}.");
        }

        var input = ResolveNode(incoming[0], context);
        if (!input.IsSuccess)
        {
            return input;
        }

        var settings = settingsValidator.Parse(node);
        return WorkflowTabularSchemaResult.Success(transform(input.Schema!, settings));
    }

    private WorkflowTabularSchemaResult ResolveJoin(
        WorkflowNode node,
        ResolutionContext context)
    {
        var settings = (JoinNodeSettings)settingsValidator.Parse(node);
        var incoming = IncomingNodeIds(node.Id, context.Graph);
        if (incoming.Count != 2
            || string.Equals(
                settings.LeftNodeId,
                settings.RightNodeId,
                StringComparison.OrdinalIgnoreCase)
            || !incoming.Contains(settings.LeftNodeId, StringComparer.OrdinalIgnoreCase)
            || !incoming.Contains(settings.RightNodeId, StringComparer.OrdinalIgnoreCase))
        {
            return WorkflowTabularSchemaResult.Failure(
                "schema.join.input",
                $"Join node '{node.Id}' inputs do not match its configured left and right node ids.");
        }

        var left = ResolveNode(settings.LeftNodeId, context);
        if (!left.IsSuccess)
        {
            return left;
        }
        var right = ResolveNode(settings.RightNodeId, context);
        if (!right.IsSuccess)
        {
            return right;
        }

        ValidateJoinKeys(left.Schema!, right.Schema!, settings);
        return WorkflowTabularSchemaResult.Success(
            WorkflowTransformSchemaSemantics.CreateJoinSchema(
                left.Schema!,
                right.Schema!,
                settings.JoinType));
    }

    private WorkflowTabularSchemaResult ResolveUnion(
        WorkflowNode node,
        ResolutionContext context)
    {
        var settings = (UnionRowsNodeSettings)settingsValidator.Parse(node);
        var incoming = IncomingNodeIds(node.Id, context.Graph);
        if (incoming.Count != settings.InputNodeIds.Count
            || incoming.Except(
                    settings.InputNodeIds,
                    StringComparer.OrdinalIgnoreCase)
                .Any()
            || settings.InputNodeIds.Except(
                    incoming,
                    StringComparer.OrdinalIgnoreCase)
                .Any())
        {
            return WorkflowTabularSchemaResult.Failure(
                "schema.union.input",
                $"Union node '{node.Id}' inputs do not match its configured input node ids.");
        }

        var schemas = new List<TabularSchema>(settings.InputNodeIds.Count);
        foreach (var inputNodeId in settings.InputNodeIds)
        {
            var input = ResolveNode(inputNodeId, context);
            if (!input.IsSuccess)
            {
                return input;
            }
            schemas.Add(input.Schema!);
        }

        var plan = WorkflowTransformSchemaSemantics.CreateUnionPlan(
            schemas,
            settings.MatchBy);
        return WorkflowTabularSchemaResult.Success(plan.Schema);
    }

    private static TabularSchema Select(
        TabularSchema input,
        SelectColumnsNodeSettings settings) =>
        new(settings.Columns.Select(name => FindColumn(input, name)));

    private static TabularSchema Filter(
        TabularSchema input,
        FilterRowsNodeSettings settings)
    {
        foreach (var condition in settings.Conditions)
        {
            _ = FindColumn(input, condition.Column);
        }
        return input;
    }

    private static TabularSchema Distinct(
        TabularSchema input,
        DistinctRowsNodeSettings settings)
    {
        foreach (var column in settings.Columns ?? [])
        {
            _ = FindColumn(input, column);
        }
        return input;
    }

    private static TabularSchema Sort(
        TabularSchema input,
        SortRowsNodeSettings settings)
    {
        foreach (var key in settings.Keys)
        {
            _ = FindColumn(input, key.Column);
        }
        return input;
    }

    private static TabularSchema Derive(
        TabularSchema input,
        DeriveColumnsNodeSettings settings)
    {
        var names = input.Columns
            .Select(column => column.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columns = new List<TabularColumn>(input.Columns);
        foreach (var derived in settings.Columns)
        {
            if (!names.Add(derived.Name))
            {
                throw new SchemaResolutionException(
                    "schema.derive.column.duplicate",
                    $"Derived column '{derived.Name}' duplicates an input column; column replacement is not supported.");
            }
            columns.Add(new(
                derived.Name,
                derived.DataType,
                derived.IsNullable));
        }
        return new(columns);
    }

    private static void ValidateJoinKeys(
        TabularSchema left,
        TabularSchema right,
        JoinNodeSettings settings)
    {
        for (var index = 0; index < settings.LeftKeys.Count; index++)
        {
            var leftColumn = FindColumn(left, settings.LeftKeys[index]);
            var rightColumn = FindColumn(right, settings.RightKeys[index]);
            if (leftColumn.DataType != rightColumn.DataType)
            {
                throw new SchemaResolutionException(
                    "schema.join.key.type",
                    $"Join keys '{leftColumn.Name}' and '{rightColumn.Name}' have incompatible types {leftColumn.DataType} and {rightColumn.DataType}.");
            }
        }
    }

    private static TabularColumn FindColumn(TabularSchema schema, string name)
    {
        var column = schema.Columns.FirstOrDefault(
            candidate => string.Equals(
                candidate.Name,
                name,
                StringComparison.OrdinalIgnoreCase));
        return column ?? throw new SchemaResolutionException(
            "schema.column.unresolved",
            $"Column '{name}' does not exist in the input schema.");
    }

    private static IReadOnlyList<string> IncomingNodeIds(
        string nodeId,
        WorkflowGraph graph) =>
        graph.Edges
            .Where(edge => string.Equals(
                edge.ToNodeId,
                nodeId,
                StringComparison.OrdinalIgnoreCase))
            .Select(edge => edge.FromNodeId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool IsSource(WorkflowNodeKind kind) =>
        IsRelationalSource(kind)
            || kind is WorkflowNodeKind.FtpSource
            or WorkflowNodeKind.CsvSource
            or WorkflowNodeKind.ExcelSource;

    private static bool IsRelationalSource(WorkflowNodeKind kind) =>
        kind is WorkflowNodeKind.SqlSource
            or WorkflowNodeKind.MySqlSource
            or WorkflowNodeKind.PostgreSqlSource
            or WorkflowNodeKind.OracleSource;

    private sealed class ResolutionContext(
        WorkflowGraph graph,
        IReadOnlyDictionary<string, WorkflowNode> nodes,
        IReadOnlyDictionary<string, TabularSchema> sourceSchemas)
    {
        public WorkflowGraph Graph { get; } = graph;

        public IReadOnlyDictionary<string, WorkflowNode> Nodes { get; } = nodes;

        public IReadOnlyDictionary<string, TabularSchema> SourceSchemas { get; } =
            sourceSchemas;

        public Dictionary<string, WorkflowTabularSchemaResult> Results { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Visiting { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> Path { get; } = [];
    }

    private sealed class SchemaResolutionException(string code, string message)
        : Exception(message)
    {
        public string Code { get; } = code;
    }
}
