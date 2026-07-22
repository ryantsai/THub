namespace THub.Domain.Workflows;

public sealed record WorkflowNode(
    string Id,
    WorkflowNodeKind Kind,
    string Name,
    double X,
    double Y,
    string SettingsJson = "{}");

