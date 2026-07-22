namespace THub.Domain.Workflows;

public sealed class WorkflowDefinition
{
    private WorkflowDefinition() { }

    public WorkflowDefinition(string name, string owner, string graphJson)
    {
        Id = Guid.NewGuid();
        Rename(name);
        Owner = Require(owner, nameof(owner), 256);
        GraphJson = Require(graphJson, nameof(graphJson), 2_000_000);
        CreatedAtUtc = DateTimeOffset.UtcNow;
        UpdatedAtUtc = CreatedAtUtc;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string Owner { get; private set; } = string.Empty;
    public WorkflowStatus Status { get; private set; } = WorkflowStatus.Draft;
    public int Version { get; private set; } = 1;
    public string GraphJson { get; private set; } = "{}";
    public string? CronExpression { get; private set; }
    public string TimeZoneId { get; private set; } = TimeZoneInfo.Utc.Id;
    public DateTimeOffset? NextRunAtUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void Rename(string name)
    {
        Name = Require(name, nameof(name), 200);
        Touch();
    }

    public void UpdateGraph(string graphJson)
    {
        GraphJson = Require(graphJson, nameof(graphJson), 2_000_000);
        Version++;
        Status = WorkflowStatus.Draft;
        Touch();
    }

    public void SetSchedule(string? cronExpression, string timeZoneId, DateTimeOffset? nextRunAtUtc)
    {
        CronExpression = string.IsNullOrWhiteSpace(cronExpression) ? null : cronExpression.Trim();
        TimeZoneId = Require(timeZoneId, nameof(timeZoneId), 200);
        NextRunAtUtc = nextRunAtUtc;
        Touch();
    }

    public void Publish() { Status = WorkflowStatus.Published; Touch(); }
    public void Pause() { Status = WorkflowStatus.Paused; NextRunAtUtc = null; Touch(); }
    public void MarkScheduled(DateTimeOffset? nextRunAtUtc) { NextRunAtUtc = nextRunAtUtc; Touch(); }

    private void Touch() => UpdatedAtUtc = DateTimeOffset.UtcNow;

    private static string Require(string value, string parameterName, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Value cannot exceed {maxLength} characters.");
        }

        return value.Trim();
    }
}

