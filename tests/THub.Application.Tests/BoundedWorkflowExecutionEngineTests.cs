using System.Runtime.CompilerServices;
using THub.Application.Execution;
using THub.Application.Workflows;
using THub.Domain.Runs;
using THub.Domain.Workflows;

namespace THub.Application.Tests;

public sealed class BoundedWorkflowExecutionEngineTests
{
    private static readonly TabularSchema SingleColumnSchema = new(
        [new TabularColumn("Id", TabularDataType.Int64, isNullable: false)]);

    [Fact]
    public async Task ExecutesSourceTargetAndEmailActionThroughGenericRegistry()
    {
        var targetRows = 0L;
        var emailRows = 0L;
        string? targetSource = null;
        string? emailSource = null;
        var executors = new IWorkflowNodeExecutor[]
        {
            Source("source", _ => ValueTask.FromResult(Output(1, 2, 3))),
            Target(
                WorkflowNodeKind.SqlTarget,
                "target",
                async (context, cancellationToken) =>
                {
                    targetSource = context.Inputs[0].SourceNodeId;
                    targetRows = await CountRowsAsync(context.Inputs[0].DataSet, cancellationToken);
                    return WorkflowNodeExecutionResult.WithoutOutput;
                }),
            new DelegateExecutor(
                WorkflowNodeExecutorDescriptor.Action(WorkflowNodeKind.EmailAlert),
                async (context, cancellationToken) =>
                {
                    emailSource = context.Inputs[0].SourceNodeId;
                    emailRows = await CountRowsAsync(context.Inputs[0].DataSet, cancellationToken);
                    return WorkflowNodeExecutionResult.WithoutOutput;
                })
        };
        var graph = new WorkflowGraph(
            [
                Node("source", WorkflowNodeKind.SqlSource),
                Node("target", WorkflowNodeKind.SqlTarget),
                Node("email", WorkflowNodeKind.EmailAlert)
            ],
            [new("source", "target"), new("source", "email")]);

        var result = await CreateEngine(executors).ExecuteAsync(
            Guid.NewGuid(),
            graph,
            CancellationToken.None);

        Assert.Equal(WorkflowExecutionStatus.Succeeded, result.Status);
        Assert.All(result.NodeOutcomes, outcome =>
            Assert.Equal(WorkflowNodeExecutionStatus.Succeeded, outcome.Status));
        Assert.Equal(3, targetRows);
        Assert.Equal(3, emailRows);
        Assert.Equal("source", targetSource);
        Assert.Equal("source", emailSource);
    }

    [Fact]
    public async Task RetriesTransientFailureOnlyWhenExecutorIsRetrySafe()
    {
        var sourceCalls = 0;
        var events = new RecordingEventSink();
        var source = Source(
            "source",
            _ =>
            {
                sourceCalls++;
                if (sourceCalls == 1)
                {
                    throw new WorkflowNodeExecutionException(new ExecutionError(
                        "source.transient",
                        ExecutionErrorCategory.Connectivity,
                        "The approved source is temporarily unavailable.",
                        isRetryable: true));
                }

                return ValueTask.FromResult(Output(42));
            });
        var graph = SourceToTargetGraph();

        var result = await CreateEngine(
            [source, NoOpTarget()],
            eventSink: events,
            retryPolicy: new NodeRetryPolicy(3, TimeSpan.Zero, TimeSpan.Zero, 0))
            .ExecuteAsync(Guid.NewGuid(), graph, CancellationToken.None);

        Assert.Equal(WorkflowExecutionStatus.Succeeded, result.Status);
        Assert.Equal(2, sourceCalls);
        Assert.Equal(2, result.NodeOutcomes.Single(outcome => outcome.NodeId == "source").Attempts);
        Assert.Contains(events.Events, executionEvent =>
            executionEvent.Kind == WorkflowExecutionEventKind.NodeRetryScheduled
            && executionEvent.NodeId == "source");
    }

    [Fact]
    public async Task DoesNotAutomaticallyRetryExternalSideEffectByDefault()
    {
        var targetCalls = 0;
        var target = Target(
            WorkflowNodeKind.SqlTarget,
            "target",
            (_, _) =>
            {
                targetCalls++;
                throw new WorkflowNodeExecutionException(new ExecutionError(
                    "target.ambiguous",
                    ExecutionErrorCategory.ExternalSideEffect,
                    "The target outcome is ambiguous.",
                    isRetryable: true));
            });

        var result = await CreateEngine(
            [Source("source", _ => ValueTask.FromResult(Output(1))), target],
            retryPolicy: new NodeRetryPolicy(3, TimeSpan.Zero, TimeSpan.Zero, 0))
            .ExecuteAsync(
                Guid.NewGuid(),
                SourceToTargetGraph(),
                CancellationToken.None);

        Assert.Equal(WorkflowExecutionStatus.Failed, result.Status);
        Assert.Equal(1, targetCalls);
        var outcome = result.NodeOutcomes.Single(item => item.NodeId == "target");
        Assert.Equal(1, outcome.Attempts);
        Assert.Equal("target.ambiguous", outcome.Error?.Code);
    }

    [Fact]
    public async Task DoesNotRetryNonTransientCategoryEvenWhenExecutorMarksItRetryable()
    {
        var sourceCalls = 0;
        var source = Source(
            "source",
            _ =>
            {
                sourceCalls++;
                throw new WorkflowNodeExecutionException(new ExecutionError(
                    "source.bad_data",
                    ExecutionErrorCategory.Data,
                    "The source data violates the approved schema.",
                    isRetryable: true));
            });

        var result = await CreateEngine(
            [source, NoOpTarget()],
            retryPolicy: new NodeRetryPolicy(3, TimeSpan.Zero, TimeSpan.Zero, 0))
            .ExecuteAsync(Guid.NewGuid(), SourceToTargetGraph(), CancellationToken.None);

        Assert.Equal(WorkflowExecutionStatus.Failed, result.Status);
        Assert.Equal(1, sourceCalls);
    }

    [Fact]
    public async Task FailedBranchIsSkippedWhileIndependentBranchCompletes()
    {
        var independentTargetCalls = 0;
        var skippedTargetCalls = 0;
        var executors = new IWorkflowNodeExecutor[]
        {
            SourceFor(WorkflowNodeKind.SqlSource, _ => ValueTask.FromResult(Output(1))),
            SourceFor(WorkflowNodeKind.CsvSource, _ => ValueTask.FromResult(Output(2))),
            new DelegateExecutor(
                WorkflowNodeExecutorDescriptor.Transform(WorkflowNodeKind.FilterRows),
                (_, _) => throw new WorkflowNodeExecutionException(new ExecutionError(
                    "filter.invalid_data",
                    ExecutionErrorCategory.Data,
                    "The input does not satisfy the configured filter contract.",
                    isRetryable: false))),
            Target(
                WorkflowNodeKind.SqlTarget,
                "unused-name",
                (_, _) =>
                {
                    skippedTargetCalls++;
                    return ValueTask.FromResult(WorkflowNodeExecutionResult.WithoutOutput);
                }),
            Target(
                WorkflowNodeKind.CsvTarget,
                "unused-name",
                (_, _) =>
                {
                    independentTargetCalls++;
                    return ValueTask.FromResult(WorkflowNodeExecutionResult.WithoutOutput);
                })
        };
        var graph = new WorkflowGraph(
            [
                Node("source-a", WorkflowNodeKind.SqlSource),
                Node("filter", WorkflowNodeKind.FilterRows),
                Node("target-skipped", WorkflowNodeKind.SqlTarget),
                Node("source-b", WorkflowNodeKind.CsvSource),
                Node("target-independent", WorkflowNodeKind.CsvTarget)
            ],
            [
                new("source-a", "filter"),
                new("filter", "target-skipped"),
                new("source-b", "target-independent")
            ]);

        var result = await CreateEngine(executors).ExecuteAsync(
            Guid.NewGuid(),
            graph,
            CancellationToken.None);

        Assert.Equal(WorkflowExecutionStatus.Failed, result.Status);
        Assert.Equal(1, independentTargetCalls);
        Assert.Equal(0, skippedTargetCalls);
        Assert.Equal(
            WorkflowNodeExecutionStatus.Failed,
            result.NodeOutcomes.Single(outcome => outcome.NodeId == "filter").Status);
        var skipped = result.NodeOutcomes.Single(outcome => outcome.NodeId == "target-skipped");
        Assert.Equal(WorkflowNodeExecutionStatus.Skipped, skipped.Status);
        Assert.Equal("dependency.not_succeeded", skipped.ReasonCode);
    }

    [Fact]
    public async Task CancellationStopsCurrentNodeAndMarksRemainingNodesCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var source = Source(
            "source",
            async context =>
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
                return Output(context.Attempt);
            });

        var result = await CreateEngine([source, NoOpTarget()]).ExecuteAsync(
            Guid.NewGuid(),
            SourceToTargetGraph(),
            cancellation.Token);

        Assert.Equal(WorkflowExecutionStatus.Cancelled, result.Status);
        Assert.All(result.NodeOutcomes, outcome =>
            Assert.Equal(WorkflowNodeExecutionStatus.Cancelled, outcome.Status));
    }

    [Fact]
    public async Task OutputThatExceedsBatchRowLimitFailsBeforeDownstreamExecution()
    {
        var targetCalls = 0;
        var owner = new RecordingAsyncDisposable();
        var limits = new TabularExecutionLimits(
            maximumRowsPerBatch: 1,
            maximumRowsPerNodeOutput: 10);
        var source = Source(
            "source",
            _ => ValueTask.FromResult(WorkflowNodeExecutionResult.WithOutput(
                SingleColumnSchema,
                OversizedBatch(owner))));
        var target = Target(
            WorkflowNodeKind.SqlTarget,
            "target",
            (_, _) =>
            {
                targetCalls++;
                return ValueTask.FromResult(WorkflowNodeExecutionResult.WithoutOutput);
            });

        var result = await CreateEngine([source, target], limits: limits).ExecuteAsync(
            Guid.NewGuid(),
            SourceToTargetGraph(),
            CancellationToken.None);

        Assert.Equal(WorkflowExecutionStatus.Failed, result.Status);
        Assert.Equal(0, targetCalls);
        Assert.True(owner.Disposed);
        var sourceOutcome = result.NodeOutcomes.Single(outcome => outcome.NodeId == "source");
        Assert.Equal(ExecutionErrorCategory.ResourceLimit, sourceOutcome.Error?.Category);
        Assert.Equal("tabular.batch.rows.limit", sourceOutcome.Error?.Code);
        Assert.Equal(
            WorkflowNodeExecutionStatus.Skipped,
            result.NodeOutcomes.Single(outcome => outcome.NodeId == "target").Status);
    }

    [Fact]
    public async Task MissingTrustedActionExecutorFailsBeforeAnyNodeRuns()
    {
        var sourceCalls = 0;
        var graph = new WorkflowGraph(
            [
                Node("source", WorkflowNodeKind.SqlSource),
                Node("webhook", WorkflowNodeKind.Webhook)
            ],
            [new("source", "webhook")]);

        var result = await CreateEngine(
            [
                Source(
                    "source",
                    _ =>
                    {
                        sourceCalls++;
                        return ValueTask.FromResult(Output(1));
                    })
        ]).ExecuteAsync(Guid.NewGuid(), graph, CancellationToken.None);

        Assert.Equal(WorkflowExecutionStatus.Failed, result.Status);
        Assert.Equal("executor.missing", result.Error?.Code);
        Assert.Equal(0, sourceCalls);
    }

    [Fact]
    public async Task SerializedExecutionRejectsUnsupportedGraphDocument()
    {
        var result = await CreateEngine([]).ExecuteSerializedAsync(
            Guid.NewGuid(),
            "{\"schemaVersion\":99,\"nodes\":[],\"edges\":[]}",
            CancellationToken.None);

        Assert.Equal(WorkflowExecutionStatus.Failed, result.Status);
        Assert.Equal("workflow.graph.serialization.invalid", result.Error?.Code);
    }

    [Fact]
    public async Task AttemptTimeoutIsNormalizedAndPreventsDownstreamExecution()
    {
        var targetCalls = 0;
        var source = new DelegateExecutor(
            WorkflowNodeExecutorDescriptor.Source(WorkflowNodeKind.SqlSource),
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Output(1);
            });
        var target = Target(
            WorkflowNodeKind.SqlTarget,
            "target",
            (_, _) =>
            {
                targetCalls++;
                return ValueTask.FromResult(WorkflowNodeExecutionResult.WithoutOutput);
            });

        var result = await CreateEngine(
            [source, target],
            retryPolicy: NodeRetryPolicy.NoRetry,
            timeoutProvider: new FixedTimeoutProvider(TimeSpan.FromMilliseconds(20)))
            .ExecuteAsync(Guid.NewGuid(), SourceToTargetGraph(), CancellationToken.None);

        Assert.Equal(WorkflowExecutionStatus.Failed, result.Status);
        Assert.Equal("execution.node.timeout", result.Error?.Code);
        Assert.Equal(0, targetCalls);
    }

    [Fact]
    public async Task CancellationArrivingAfterAllNodeWorkDoesNotRewriteSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        var sink = new CancelAfterTargetSucceededSink(cancellation);

        var result = await CreateEngine(
            [Source("source", _ => ValueTask.FromResult(Output(1))), NoOpTarget()],
            eventSink: sink).ExecuteAsync(
                Guid.NewGuid(),
                SourceToTargetGraph(),
                cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(WorkflowExecutionStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task TerminalEventSinkFailureReturnsDeterministicFailure()
    {
        var result = await CreateEngine(
            [Source("source", _ => ValueTask.FromResult(Output(1))), NoOpTarget()],
            eventSink: new FailingTerminalEventSink()).ExecuteAsync(
                Guid.NewGuid(),
                SourceToTargetGraph(),
                CancellationToken.None);

        Assert.Equal(WorkflowExecutionStatus.Failed, result.Status);
        Assert.Equal("execution.event_sink.unavailable", result.Error?.Code);
    }

    [Fact]
    public async Task InvalidTypedSettingsAnywherePreventAllExecutorEffects()
    {
        var sourceCalls = 0;
        var actionCalls = 0;
        var connectionId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var sourceSettings =
            $$"""{"connectionId":"{{connectionId:D}}","schema":"dbo","object":"Source","batchSize":100}""";
        var emailSettings =
            $$"""{"profileId":"{{profileId:D}}","recipients":["ops@example.test"],"subject":"Done","body":"","maximumAttempts":1}""";
        var graph = new WorkflowGraph(
            [
                new WorkflowNode("source-action", WorkflowNodeKind.SqlSource, "source-action", 0, 0, sourceSettings),
                new WorkflowNode("email", WorkflowNodeKind.EmailAlert, "email", 0, 0, emailSettings),
                new WorkflowNode("source-target", WorkflowNodeKind.SqlSource, "source-target", 0, 0, sourceSettings),
                new WorkflowNode("invalid-target", WorkflowNodeKind.SqlTarget, "invalid-target", 0, 0, "{}")
            ],
            [
                new WorkflowEdge("source-action", "email"),
                new WorkflowEdge("source-target", "invalid-target")
            ]);
        var preflight = new WorkflowExecutionPreflightValidator(
            new WorkflowNodeSettingsValidator(),
            [],
            new DefaultExecutionErrorClassifier());
        var executors = new IWorkflowNodeExecutor[]
        {
            Source(
                "source",
                _ =>
                {
                    sourceCalls++;
                    return ValueTask.FromResult(Output(1));
                }),
            new DelegateExecutor(
                WorkflowNodeExecutorDescriptor.Action(WorkflowNodeKind.EmailAlert),
                (_, _) =>
                {
                    actionCalls++;
                    return ValueTask.FromResult(WorkflowNodeExecutionResult.WithoutOutput);
                }),
            NoOpTarget()
        };

        var result = await CreateEngine(executors, preflightValidator: preflight).ExecuteAsync(
            Guid.NewGuid(),
            graph,
            CancellationToken.None);

        Assert.Equal(WorkflowExecutionStatus.Failed, result.Status);
        Assert.Equal("node.settings.text.required", result.Error?.Code);
        Assert.Equal(0, sourceCalls);
        Assert.Equal(0, actionCalls);
        Assert.All(result.NodeOutcomes, outcome =>
            Assert.Equal(WorkflowNodeExecutionStatus.Skipped, outcome.Status));
    }

    [Fact]
    public async Task InvalidResourceReferencePreventsAllExecutorEffects()
    {
        var sourceCalls = 0;
        var connectionId = Guid.NewGuid();
        var sourceSettings =
            $$"""{"connectionId":"{{connectionId:D}}","schema":"dbo","object":"Source","batchSize":100}""";
        var targetSettings =
            $"{{\"connectionId\":\"{connectionId:D}\",\"schema\":\"dbo\",\"object\":\"Target\",\"mode\":\"insert\",\"bindings\":[]}}";
        var graph = new WorkflowGraph(
            [
                new WorkflowNode("source", WorkflowNodeKind.SqlSource, "source", 0, 0, sourceSettings),
                new WorkflowNode("target", WorkflowNodeKind.SqlTarget, "target", 0, 0, targetSettings)
            ],
            [new WorkflowEdge("source", "target")]);
        var preflight = new WorkflowExecutionPreflightValidator(
            new WorkflowNodeSettingsValidator(),
            [new RejectingResourceValidator("target")],
            new DefaultExecutionErrorClassifier());

        var result = await CreateEngine(
            [
                Source(
                    "source",
                    _ =>
                    {
                        sourceCalls++;
                        return ValueTask.FromResult(Output(1));
                    }),
                NoOpTarget()
            ],
            preflightValidator: preflight).ExecuteAsync(
                Guid.NewGuid(),
                graph,
                CancellationToken.None);

        Assert.Equal(WorkflowExecutionStatus.Failed, result.Status);
        Assert.Equal("execution.connection.disabled", result.Error?.Code);
        Assert.Equal(0, sourceCalls);
    }

    private static BoundedWorkflowExecutionEngine CreateEngine(
        IEnumerable<IWorkflowNodeExecutor> executors,
        IWorkflowExecutionEventSink? eventSink = null,
        NodeRetryPolicy? retryPolicy = null,
        TabularExecutionLimits? limits = null,
        INodeExecutionTimeoutProvider? timeoutProvider = null,
        IWorkflowExecutionPreflightValidator? preflightValidator = null) => new(
            new WorkflowExecutionPlanner(new WorkflowGraphValidator()),
            new WorkflowNodeExecutorRegistry(executors),
            preflightValidator ?? new PassThroughPreflightValidator(),
            new InMemoryTabularDataSetStore(),
            new DefaultNodeRetryPolicyProvider(retryPolicy),
            new ImmediateRetryScheduler(),
            new DefaultExecutionErrorClassifier(),
            eventSink ?? new RecordingEventSink(),
            limits,
            timeoutProvider: timeoutProvider);

    private static WorkflowGraph SourceToTargetGraph() => new(
        [
            Node("source", WorkflowNodeKind.SqlSource),
            Node("target", WorkflowNodeKind.SqlTarget)
        ],
        [new("source", "target")]);

    private static WorkflowNode Node(string id, WorkflowNodeKind kind) =>
        new(id, kind, id, 0, 0);

    private static DelegateExecutor Source(
        string unusedNodeId,
        Func<WorkflowNodeExecutionContext, ValueTask<WorkflowNodeExecutionResult>> execute)
    {
        _ = unusedNodeId;
        return SourceFor(WorkflowNodeKind.SqlSource, execute);
    }

    private static DelegateExecutor SourceFor(
        WorkflowNodeKind kind,
        Func<WorkflowNodeExecutionContext, ValueTask<WorkflowNodeExecutionResult>> execute) => new(
            WorkflowNodeExecutorDescriptor.Source(kind),
            (context, _) => execute(context));

    private static DelegateExecutor Target(
        WorkflowNodeKind kind,
        string unusedNodeId,
        Func<WorkflowNodeExecutionContext, CancellationToken, ValueTask<WorkflowNodeExecutionResult>> execute)
    {
        _ = unusedNodeId;
        return new DelegateExecutor(WorkflowNodeExecutorDescriptor.Target(kind), execute);
    }

    private static DelegateExecutor NoOpTarget() => Target(
        WorkflowNodeKind.SqlTarget,
        "target",
        (_, _) => ValueTask.FromResult(WorkflowNodeExecutionResult.WithoutOutput));

    private static WorkflowNodeExecutionResult Output(params long[] values) =>
        WorkflowNodeExecutionResult.WithOutput(SingleColumnSchema, Batches(values));

    private static async IAsyncEnumerable<TabularBatch> Batches(
        IEnumerable<long> values,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new TabularBatch(
            values.Select(value => new TabularRow([TabularValue.From(value)])));
    }

    private static async IAsyncEnumerable<TabularBatch> OversizedBatch(
        RecordingAsyncDisposable owner,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new TabularBatch(
            [
                new TabularRow([TabularValue.From(1L)]),
                new TabularRow([TabularValue.From(2L)])
            ],
            owner);
    }

    private static async ValueTask<long> CountRowsAsync(
        ITabularDataSet dataSet,
        CancellationToken cancellationToken)
    {
        long rows = 0;
        await foreach (var batch in dataSet.ReadBatchesAsync(cancellationToken))
        {
            await using (batch)
            {
                rows += batch.Rows.Count;
            }
        }

        return rows;
    }

    private sealed class DelegateExecutor : IWorkflowNodeExecutor
    {
        private readonly Func<WorkflowNodeExecutionContext, CancellationToken, ValueTask<WorkflowNodeExecutionResult>>
            _execute;

        public DelegateExecutor(
            WorkflowNodeExecutorDescriptor descriptor,
            Func<WorkflowNodeExecutionContext, CancellationToken, ValueTask<WorkflowNodeExecutionResult>> execute)
        {
            Descriptor = descriptor;
            _execute = execute;
        }

        public WorkflowNodeExecutorDescriptor Descriptor { get; }

        public ValueTask<WorkflowNodeExecutionResult> ExecuteAsync(
            WorkflowNodeExecutionContext context,
            CancellationToken cancellationToken) =>
            _execute(context, cancellationToken);
    }

    private sealed class RecordingEventSink : IWorkflowExecutionEventSink
    {
        public List<WorkflowExecutionEvent> Events { get; } = [];

        public ValueTask WriteAsync(
            WorkflowExecutionEvent executionEvent,
            CancellationToken cancellationToken)
        {
            Events.Add(executionEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ImmediateRetryScheduler : IWorkflowRetryScheduler
    {
        public TimeSpan GetDelay(NodeRetryPolicy policy, int failedAttempt) => TimeSpan.Zero;

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingAsyncDisposable : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeoutProvider : INodeExecutionTimeoutProvider
    {
        private readonly TimeSpan _timeout;

        public FixedTimeoutProvider(TimeSpan timeout)
        {
            _timeout = timeout;
        }

        public TimeSpan GetAttemptTimeout(
            WorkflowNode node,
            WorkflowNodeExecutorDescriptor executorDescriptor) => _timeout;
    }

    private sealed class CancelAfterTargetSucceededSink : IWorkflowExecutionEventSink
    {
        private readonly CancellationTokenSource _cancellation;

        public CancelAfterTargetSucceededSink(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public ValueTask WriteAsync(
            WorkflowExecutionEvent executionEvent,
            CancellationToken cancellationToken)
        {
            if (executionEvent.Kind == WorkflowExecutionEventKind.NodeSucceeded
                && executionEvent.NodeId == "target")
            {
                _cancellation.Cancel();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingTerminalEventSink : IWorkflowExecutionEventSink
    {
        public ValueTask WriteAsync(
            WorkflowExecutionEvent executionEvent,
            CancellationToken cancellationToken)
        {
            if (executionEvent.Kind == WorkflowExecutionEventKind.ExecutionSucceeded)
            {
                throw new IOException("Simulated durable event store outage.");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class PassThroughPreflightValidator : IWorkflowExecutionPreflightValidator
    {
        public ValueTask<ExecutionError?> ValidateAsync(
            WorkflowGraph graph,
            TabularExecutionLimits limits,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ExecutionError?>(null);
        }
    }

    private sealed class RejectingResourceValidator(string rejectedNodeId)
        : IWorkflowNodeResourceValidator
    {
        public ValueTask ValidateAsync(
            WorkflowNode node,
            WorkflowNodeSettings settings,
            TabularExecutionLimits limits,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.Id == rejectedNodeId)
            {
                throw new WorkflowNodeExecutionException(new ExecutionError(
                    "execution.connection.disabled",
                    ExecutionErrorCategory.Configuration,
                    "The configured connection is disabled.",
                    isRetryable: false));
            }

            return ValueTask.CompletedTask;
        }
    }
}
