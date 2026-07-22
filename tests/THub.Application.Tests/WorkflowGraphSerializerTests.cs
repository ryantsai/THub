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
            []);

        var json = serializer.Serialize(graph);
        var restored = serializer.Deserialize(json);

        Assert.Contains("\"schemaVersion\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"settings\":{\"table\":\"Customers\"}", json, StringComparison.Ordinal);
        Assert.Equal(graph.Nodes[0] with { SettingsJson = "{\"table\":\"Customers\"}" }, restored.Nodes[0]);
    }

    [Fact]
    public void RejectsUnsupportedSchemaVersion()
    {
        var exception = Assert.Throws<WorkflowGraphSerializationException>(() =>
            serializer.Deserialize("{\"schemaVersion\":2,\"nodes\":[],\"edges\":[]}"));

        Assert.Contains("not supported", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsUnknownNodeProperties()
    {
        const string json = """
            {
              "schemaVersion": 1,
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
              "schemaVersion": 1,
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

