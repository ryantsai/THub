namespace THub.Application.Scheduling;

public sealed record WorkflowSchedule(
    Guid WorkflowId,
    int WorkflowVersion,
    string CronExpression,
    string TimeZoneId,
    DateTimeOffset? NextRunAtUtc);
