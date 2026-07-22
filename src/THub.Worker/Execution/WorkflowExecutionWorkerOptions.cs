using System.ComponentModel.DataAnnotations;
using THub.Application.Execution;

namespace THub.Worker.Execution;

public sealed class WorkflowExecutionWorkerOptions
{
    public const string SectionName = "Execution";

    [Range(1, 32)]
    public int MaximumConcurrency { get; set; } = 2;

    [Range(100, 60_000)]
    public int PollIntervalMilliseconds { get; set; } = 1_000;

    [Range(15, 3_600)]
    public int LeaseDurationSeconds { get; set; } = 60;

    [Range(5, 1_200)]
    public int HeartbeatIntervalSeconds { get; set; } = 15;

    [Range(1, 1_440)]
    public int MaximumRunDurationMinutes { get; set; } = 720;

    [Range(1, 1_440)]
    public int NodeAttemptTimeoutMinutes { get; set; } = 30;

    [Range(1, TabularExecutionLimits.AbsoluteMaximumColumns)]
    public int MaximumColumns { get; set; } = 256;

    [Range(1, TabularExecutionLimits.AbsoluteMaximumRowsPerBatch)]
    public int MaximumRowsPerBatch { get; set; } = 5_000;

    [Range(1, TabularBatch.AbsoluteMaximumBytes)]
    public long MaximumBytesPerBatch { get; set; } = 8L * 1_024 * 1_024;

    [Range(1, TabularExecutionLimits.AbsoluteMaximumRowsPerOutput)]
    public long MaximumRowsPerNodeOutput { get; set; } = 1_000_000;

    [Range(1, TabularExecutionLimits.AbsoluteMaximumBytesPerOutput)]
    public long MaximumBytesPerNodeOutput { get; set; } = 512L * 1_024 * 1_024;

    [Range(1, TabularExecutionLimits.AbsoluteMaximumRowsPerOutput)]
    public long MaximumRetainedRowsPerWorkflow { get; set; } = 3_000_000;

    [Range(1, TabularExecutionLimits.AbsoluteMaximumBytesPerOutput)]
    public long MaximumRetainedBytesPerWorkflow { get; set; } = 1_536L * 1_024 * 1_024;

    public TimeSpan PollInterval => TimeSpan.FromMilliseconds(PollIntervalMilliseconds);

    public TimeSpan LeaseDuration => TimeSpan.FromSeconds(LeaseDurationSeconds);

    public TimeSpan HeartbeatInterval => TimeSpan.FromSeconds(HeartbeatIntervalSeconds);

    public void ValidateCrossFieldBounds()
    {
        if (HeartbeatInterval >= LeaseDuration / 2)
        {
            throw new InvalidOperationException(
                "Execution heartbeat interval must be less than half of the lease duration.");
        }

        _ = CreateLimits();
        _ = CreateTimeouts();
    }

    public TabularExecutionLimits CreateLimits() => new(
        maximumColumns: MaximumColumns,
        maximumRowsPerBatch: MaximumRowsPerBatch,
        maximumBytesPerBatch: MaximumBytesPerBatch,
        maximumRowsPerNodeOutput: MaximumRowsPerNodeOutput,
        maximumBytesPerNodeOutput: MaximumBytesPerNodeOutput,
        maximumRetainedRowsPerWorkflow: MaximumRetainedRowsPerWorkflow,
        maximumRetainedBytesPerWorkflow: MaximumRetainedBytesPerWorkflow);

    public WorkflowExecutionTimeoutOptions CreateTimeouts() => new(
        TimeSpan.FromMinutes(MaximumRunDurationMinutes),
        TimeSpan.FromMinutes(NodeAttemptTimeoutMinutes));
}
