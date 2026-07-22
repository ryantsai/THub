using System.Text;
using System.Text.Json;
using THub.Domain.Workflows;

namespace THub.Application.Workflows;

public sealed class WorkflowGraphSerializer
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumDocumentCharacters = 2_000_000;

    private static readonly HashSet<string> RootProperties =
        ["schemaVersion", "nodes", "edges"];

    private static readonly HashSet<string> NodeProperties =
        ["id", "kind", "name", "x", "y", "settings"];

    private static readonly HashSet<string> EdgeProperties =
        ["fromNodeId", "toNodeId"];

    public string Serialize(WorkflowGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", CurrentSchemaVersion);
            writer.WriteStartArray("nodes");

            foreach (var node in graph.Nodes)
            {
                writer.WriteStartObject();
                writer.WriteString("id", node.Id);
                writer.WriteString("kind", node.Kind.ToString());
                writer.WriteString("name", node.Name);
                writer.WriteNumber("x", node.X);
                writer.WriteNumber("y", node.Y);
                writer.WritePropertyName("settings");

                try
                {
                    using var settings = JsonDocument.Parse(
                        string.IsNullOrWhiteSpace(node.SettingsJson) ? "{}" : node.SettingsJson,
                        DocumentOptions);
                    if (settings.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        throw new WorkflowGraphSerializationException(
                            $"Settings for node '{node.Id}' must be a JSON object.");
                    }

                    settings.RootElement.WriteTo(writer);
                }
                catch (JsonException exception)
                {
                    throw new WorkflowGraphSerializationException(
                        $"Settings for node '{node.Id}' are not valid JSON.",
                        exception);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("edges");
            foreach (var edge in graph.Edges)
            {
                writer.WriteStartObject();
                writer.WriteString("fromNodeId", edge.FromNodeId);
                writer.WriteString("toNodeId", edge.ToNodeId);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public WorkflowGraph Deserialize(string graphJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphJson);
        if (graphJson.Length > MaximumDocumentCharacters)
        {
            throw new WorkflowGraphSerializationException(
                $"Workflow graph JSON cannot exceed {MaximumDocumentCharacters} characters.");
        }

        try
        {
            using var document = JsonDocument.Parse(graphJson, DocumentOptions);
            var root = document.RootElement;
            RequireObject(root, "Workflow graph");
            EnsureOnlyProperties(root, RootProperties, "workflow graph");

            var schemaVersion = RequireProperty(root, "schemaVersion").GetInt32();
            if (schemaVersion != CurrentSchemaVersion)
            {
                throw new WorkflowGraphSerializationException(
                    $"Workflow graph schema version {schemaVersion} is not supported. " +
                    $"Expected version {CurrentSchemaVersion}.");
            }

            var nodesElement = RequireProperty(root, "nodes");
            var edgesElement = RequireProperty(root, "edges");
            if (nodesElement.ValueKind != JsonValueKind.Array
                || edgesElement.ValueKind != JsonValueKind.Array)
            {
                throw new WorkflowGraphSerializationException(
                    "Workflow graph nodes and edges must be JSON arrays.");
            }

            var nodes = nodesElement.EnumerateArray().Select(ReadNode).ToArray();
            var edges = edgesElement.EnumerateArray().Select(ReadEdge).ToArray();
            return new WorkflowGraph(nodes, edges);
        }
        catch (WorkflowGraphSerializationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidOperationException
            or FormatException
            or OverflowException)
        {
            throw new WorkflowGraphSerializationException(
                "Workflow graph JSON is malformed or contains a value of the wrong type.",
                exception);
        }
    }

    private static WorkflowNode ReadNode(JsonElement element)
    {
        RequireObject(element, "Workflow node");
        EnsureOnlyProperties(element, NodeProperties, "workflow node");

        var id = RequireString(element, "id");
        var kindText = RequireString(element, "kind");
        if (!Enum.TryParse<WorkflowNodeKind>(kindText, ignoreCase: false, out var kind)
            || int.TryParse(kindText, out _))
        {
            throw new WorkflowGraphSerializationException(
                $"Workflow node '{id}' has unsupported kind '{kindText}'.");
        }

        var name = RequireString(element, "name");
        var x = RequireProperty(element, "x").GetDouble();
        var y = RequireProperty(element, "y").GetDouble();
        var settings = element.TryGetProperty("settings", out var settingsElement)
            ? settingsElement
            : default;

        if (settings.ValueKind == JsonValueKind.Undefined)
        {
            return new WorkflowNode(id, kind, name, x, y);
        }

        if (settings.ValueKind != JsonValueKind.Object)
        {
            throw new WorkflowGraphSerializationException(
                $"Settings for node '{id}' must be a JSON object.");
        }

        return new WorkflowNode(id, kind, name, x, y, settings.GetRawText());
    }

    private static WorkflowEdge ReadEdge(JsonElement element)
    {
        RequireObject(element, "Workflow edge");
        EnsureOnlyProperties(element, EdgeProperties, "workflow edge");
        return new WorkflowEdge(
            RequireString(element, "fromNodeId"),
            RequireString(element, "toNodeId"));
    }

    private static JsonElement RequireProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            throw new WorkflowGraphSerializationException(
                $"Required workflow graph property '{name}' is missing.");
        }

        return value;
    }

    private static string RequireString(JsonElement element, string name)
    {
        var value = RequireProperty(element, name);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new WorkflowGraphSerializationException(
                $"Workflow graph property '{name}' must be a string.");
        }

        return value.GetString() ?? string.Empty;
    }

    private static void RequireObject(JsonElement element, string description)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new WorkflowGraphSerializationException($"{description} must be a JSON object.");
        }
    }

    private static void EnsureOnlyProperties(
        JsonElement element,
        HashSet<string> allowedProperties,
        string description)
    {
        var encountered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!encountered.Add(property.Name))
            {
                throw new WorkflowGraphSerializationException(
                    $"The {description} contains duplicate property '{property.Name}'.");
            }

            if (!allowedProperties.Contains(property.Name))
            {
                throw new WorkflowGraphSerializationException(
                    $"The {description} contains unsupported property '{property.Name}'.");
            }
        }
    }

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64
    };
}

