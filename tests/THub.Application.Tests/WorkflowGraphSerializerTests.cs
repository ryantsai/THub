using THub.Application.Workflows;
using THub.Domain.Workflows;

namespace THub.Application.Tests;

public sealed class WorkflowGraphSerializerTests
{
    private readonly WorkflowGraphSerializer serializer = new();

    [Fact]
    public void RoundTripsVersionedGraphWithSettingsObject()
    {
        var graph = new WorkflowGraph(
            [new WorkflowNode("source", WorkflowNodeKind.SqlSource, "Source", 12.5, 42, "{\"table\":\"Customers\"}")],
            [],
            [new(
                "tenant",
                WorkflowVariableKind.Literal,
                WorkflowValueType.String,
                "north")],
            [new("slug", ["value"], "String(value).toLowerCase()")]);

        var json = serializer.Serialize(graph);
        var restored = serializer.Deserialize(json);

        Assert.Contains("\"schemaVersion\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"variables\":[", json, StringComparison.Ordinal);
        Assert.Contains("\"functions\":[", json, StringComparison.Ordinal);
        Assert.Equal("tenant", Assert.Single(restored.Variables).Name);
        Assert.Equal("slug", Assert.Single(restored.Functions).Name);
        Assert.Contains("\"settings\":{\"table\":\"Customers\"}", json, StringComparison.Ordinal);
        Assert.Equal(graph.Nodes[0] with { SettingsJson = "{\"table\":\"Customers\"}" }, restored.Nodes[0]);
    }

    [Fact]
    public void RejectsUnsupportedSchemaVersion()
    {
        var exception = Assert.Throws<WorkflowGraphSerializationException>(() =>
            serializer.Deserialize("{\"schemaVersion\":1,\"variables\":[],\"functions\":[],\"nodes\":[],\"edges\":[]}"));

        Assert.Contains("not supported", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsUnknownNodeProperties()
    {
        const string json = """
            {
              "schemaVersion": 2,
              "variables": [],
              "functions": [],
              "nodes": [{
                "id": "source",
                "kind": "SqlSource",
                "name": "Source",
                "x": 0,
                "y": 0,
                "settings": {},
                "credential": "must-not-be-ignored"
              }],
              "edges": []
            }
            """;

        var exception = Assert.Throws<WorkflowGraphSerializationException>(() =>
            serializer.Deserialize(json));

        Assert.Contains("unsupported property", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsSettingsThatAreNotObjects()
    {
        const string json = """
            {
              "schemaVersion": 2,
              "variables": [],
              "functions": [],
              "nodes": [{
                "id": "source",
                "kind": "SqlSource",
                "name": "Source",
                "x": 0,
                "y": 0,
                "settings": "unsafe"
              }],
              "edges": []
            }
            """;

        Assert.Throws<WorkflowGraphSerializationException>(() => serializer.Deserialize(json));
    }

    [Fact]
    public void RejectsInvalidSettingsDuringSerialization()
    {
        var graph = new WorkflowGraph(
            [new WorkflowNode("source", WorkflowNodeKind.SqlSource, "Source", 0, 0, "not-json")],
            []);

        Assert.Throws<WorkflowGraphSerializationException>(() => serializer.Serialize(graph));
    }
}
