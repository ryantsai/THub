using System.ComponentModel.DataAnnotations;

namespace THub.Worker.Scheduling;

public sealed class SchedulerOptions
{
    public const string SectionName = "Scheduler";

    [Range(5, 300)]
    public int ReconciliationIntervalSeconds { get; init; } = 15;

    [Range(1, 100)]
    public int MaxConcurrency { get; init; } = 10;

    [Range(1, 300)]
    public int DatabaseRetryIntervalSeconds { get; init; } = 15;

    [Range(5, 300)]
    public int ClusterCheckinIntervalSeconds { get; init; } = 10;

    [Range(5, 300)]
    public int ClusterCheckinMisfireThresholdSeconds { get; init; } = 20;
}
