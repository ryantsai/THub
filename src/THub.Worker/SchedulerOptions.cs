using System.ComponentModel.DataAnnotations;

namespace THub.Worker;

public sealed class SchedulerOptions
{
    public const string SectionName = "Scheduler";

    [Range(1, 300)]
    public int PollIntervalSeconds { get; init; } = 10;

    [Range(1, 300)]
    public int ErrorRetrySeconds { get; init; } = 30;
}

