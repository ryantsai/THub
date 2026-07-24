using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using THub.Application.Alerts;
using THub.Application.Execution;
using THub.Domain.Alerts;
using THub.Domain.Runs;
using THub.Domain.Workflows;
using THub.Infrastructure.Execution;

namespace THub.Infrastructure.Tests;

public sealed class EmailTargetNodeExecutorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InlineTargetQueuesHtmlWithEncodedTabularValues()
    {
        var store = new RecordingDeliveryStore();
        var runId = Guid.NewGuid();
        var executor = CreateExecutor(store);
        var context = CreateContext(
            runId,
            "inline",
            "<p>Run {{run.id}}</p>{{data}}",
            "results.csv");

        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        Assert.Null(result.Output);
        Assert.Equal(WorkflowNodeRole.Target, executor.Descriptor.Role);
        var delivery = Assert.Single(store.Deliveries);
        Assert.True(delivery.Message.IsBodyHtml);
        Assert.Null(delivery.Message.Attachment);
        Assert.Contains($"Run {runId:D}", delivery.Message.Body);
        Assert.Contains("&lt;Admin&gt;", delivery.Message.Body);
        Assert.Contains("<table>", delivery.Message.Body);
    }

    [Fact]
    public async Task AttachmentTargetQueuesUtf8CsvAttachment()
    {
        var store = new RecordingDeliveryStore();
        var executor = CreateExecutor(store);
        var context = CreateContext(
            Guid.NewGuid(),
            "attachment",
            "<p>Attached data</p>",
            "orders.csv");

        _ = await executor.ExecuteAsync(context, CancellationToken.None);

        var attachment = Assert.IsType<EmailAttachment>(
            Assert.Single(store.Deliveries).Message.Attachment);
        Assert.Equal("orders.csv", attachment.FileName);
        var csv = Encoding.UTF8.GetString(attachment.Content.Span);
        Assert.Contains("Id,Name", csv);
        Assert.Contains("42,<Admin>", csv);
    }

    private static EmailTargetNodeExecutor CreateExecutor(RecordingDeliveryStore store) =>
        new(
            new WorkflowNodeSettingsValidator(),
            new StepRunLocator(Guid.NewGuid()),
            new EmailActionOutboxService(store, new FixedTimeProvider(Now)));

    private static WorkflowNodeExecutionContext CreateContext(
        Guid runId,
        string mode,
        string body,
        string attachmentFileName)
    {
        var settings = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["profileId"] = Guid.NewGuid(),
            ["recipients"] = new[] { "owner@example.com" },
            ["subject"] = "Workflow data {{run.id}}",
            ["body"] = body,
            ["deliveryMode"] = mode,
            ["attachmentFileName"] = attachmentFileName,
            ["maximumAttempts"] = 5
        });
        var node = new WorkflowNode(
            "email-target",
            WorkflowNodeKind.EmailTarget,
            "Email destination",
            0,
            0,
            settings);
        return new WorkflowNodeExecutionContext(
            runId,
            node,
            1,
            [new WorkflowNodeInput("source", new TestDataSet())],
            new TabularExecutionLimits(),
            new RecordingProgress());
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
        public ValueTask ReportAsync(
            WorkflowNodeProgress delta,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = delta.Validate();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestDataSet : ITabularDataSet
    {
        public TabularSchema Schema { get; } = new(
        [
            new TabularColumn("Id", TabularDataType.Int64, isNullable: false),
            new TabularColumn("Name", TabularDataType.String, isNullable: false)
        ]);

        public long RowCount => 1;

        public long ByteCount => 20;

        public async IAsyncEnumerable<TabularBatch> ReadBatchesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new TabularBatch(
            [
                new TabularRow([TabularValue.From(42L), TabularValue.From("<Admin>")])
            ]);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
