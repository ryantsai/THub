using System.Runtime.CompilerServices;
using System.Text.Json;
using THub.Application.Alerts;
using THub.Application.Execution;
using THub.Domain.Alerts;
using THub.Domain.Runs;
using THub.Domain.Workflows;
using THub.Infrastructure.Execution;

namespace THub.Infrastructure.Tests;

public sealed class EmailAlertNodeExecutorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExecuteQueuesDurableIntentUsingOnlyAllowedRunVariable()
    {
        var runId = Guid.NewGuid();
        var stepRunId = Guid.NewGuid();
        var profile = new EmailDeliveryProfile(
            "Operations relay",
            "smtp.example.com",
            587,
            EmailTransportSecurity.StartTlsRequired,
            "thub@example.com",
            ["example.com"],
            "DOMAIN\\administrator",
            Now);
        var deliveryStore = new RecordingDeliveryStore();
        var executor = new EmailAlertNodeExecutor(
            new WorkflowNodeSettingsValidator(),
            new StepRunLocator(stepRunId),
            new EmailActionOutboxService(
                deliveryStore,
                new FixedTimeProvider(Now)));
        var settings = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["profileId"] = profile.Id,
            ["recipients"] = new[] { "ops@example.com" },
            ["subject"] = "Run {{run.id}} needs attention",
            ["body"] = "Review the durable workflow alert.",
            ["maximumAttempts"] = 5
        });
        var node = new WorkflowNode(
            "email-alert",
            WorkflowNodeKind.EmailAlert,
            "Notify operations",
            0,
            0,
            settings);
        var progress = new RecordingProgress();
        var context = new WorkflowNodeExecutionContext(
            runId,
            node,
            1,
            [new WorkflowNodeInput("source", new TestDataSet(3))],
            new TabularExecutionLimits(),
            progress);

        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        Assert.Null(result.Output);
        Assert.Equal(
            WorkflowNodeRetrySafety.IdempotentSideEffect,
            executor.Descriptor.RetrySafety);
        var delivery = Assert.Single(deliveryStore.Deliveries);
        Assert.Equal(runId, delivery.WorkflowRunId);
        Assert.Equal(stepRunId, delivery.WorkflowStepRunId);
        Assert.Equal("email-alert", delivery.WorkflowNodeId);
        Assert.Equal($"Run {runId:D} needs attention", delivery.Message.Subject);
        Assert.Equal(AlertDeliveryStatus.Pending, delivery.Status);
        Assert.Equal(new WorkflowNodeProgress(RowsRead: 3, BatchesProcessed: 1), progress.Last);
    }

    private sealed class RecordingDeliveryStore : IAlertDeliveryStore
    {
        public List<AlertDelivery> Deliveries { get; } = [];

        public Task<AlertEnqueueStoreResult> EnqueueEmailActionAsync(
            AlertDelivery delivery,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Deliveries.Add(delivery);
            return Task.FromResult(new AlertEnqueueStoreResult(
                AlertEnqueueStatus.Enqueued,
                delivery.Id));
        }

        public Task<ClaimedAlertDelivery?> TryClaimNextAsync(
            string leaseOwner,
            DateTimeOffset claimedAtUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AlertDeliveryTransitionStatus> RecordDeliveredAsync(
            Guid deliveryId,
            string leaseOwner,
            DateTimeOffset deliveredAtUtc,
            string? providerMessageId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AlertDeliveryTransitionStatus> RecordFailureAsync(
            Guid deliveryId,
            string leaseOwner,
            ExecutionError error,
            DateTimeOffset failedAtUtc,
            DateTimeOffset? nextAttemptAtUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StepRunLocator(Guid stepRunId) : IWorkflowStepRunLocator
    {
        public Task<Guid?> FindRunningStepIdAsync(
            Guid workflowRunId,
            string nodeId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Guid?>(stepRunId);
        }
    }

    private sealed class RecordingProgress : IWorkflowNodeProgressReporter
    {
        public WorkflowNodeProgress? Last { get; private set; }

        public ValueTask ReportAsync(
            WorkflowNodeProgress delta,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Last = delta.Validate();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestDataSet(int rowCount) : ITabularDataSet
    {
        public TabularSchema Schema { get; } = new(
            [new TabularColumn("Id", TabularDataType.Int64, isNullable: false)]);

        public long RowCount { get; } = rowCount;

        public long ByteCount => 0;

        public async IAsyncEnumerable<TabularBatch> ReadBatchesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
