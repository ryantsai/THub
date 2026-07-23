using THub.Worker.Execution;

namespace THub.Worker.Tests;

public sealed class WorkflowExecutionWorkerOptionsTests
{
    [Fact]
    public void DefaultsProduceValidLeaseTimeoutAndTabularLimits()
    {
        var options = new WorkflowExecutionWorkerOptions();

        options.ValidateCrossFieldBounds();

        Assert.Equal(32, options.MaximumConcurrency);
        Assert.True(options.HeartbeatInterval < options.LeaseDuration / 2);
        Assert.NotNull(options.CreateLimits());
        Assert.NotNull(options.CreateTimeouts());
    }

    [Fact]
    public void HeartbeatMustLeaveLeaseRecoveryMargin()
    {
        var options = new WorkflowExecutionWorkerOptions
        {
            LeaseDurationSeconds = 60,
            HeartbeatIntervalSeconds = 30
        };

        var exception = Assert.Throws<InvalidOperationException>(options.ValidateCrossFieldBounds);

        Assert.Contains("less than half", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RetainedWorkflowLimitCannotBeSmallerThanNodeOutputLimit()
    {
        var options = new WorkflowExecutionWorkerOptions
        {
            MaximumRowsPerNodeOutput = 100,
            MaximumRetainedRowsPerWorkflow = 99,
            MaximumRowsPerBatch = 10
        };

        Assert.Throws<ArgumentException>(options.ValidateCrossFieldBounds);
    }
}
