namespace THub.Application.Workflows;

public sealed record GraphValidationIssue(string Code, string Message, string? NodeId = null);

