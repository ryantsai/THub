using THub.Application.Workflows;
using THub.Domain.Runs;
using THub.Domain.Workflows;

namespace THub.Application.Execution;

public sealed class BoundedWorkflowExecutionEngine
{
    private static readonly HashSet<WorkflowNodeKind> GatedNodeKinds =
    [
        WorkflowNodeKind.PublishRestApi,
        WorkflowNodeKind.PublishDataEditor
    ];

    private readonly WorkflowExecutionPlanner _planner;
    private readonly WorkflowNodeExecutorRegistry _executors;
    private readonly IWorkflowExecutionPreflightValidator _preflightValidator;
    private readonly ITabularDataSetStore _dataSetStore;
    private readonly INodeRetryPolicyProvider _retryPolicies;
    private readonly IWorkflowRetryScheduler _retryScheduler;
    private readonly IExecutionErrorClassifier _errorClassifier;
    private readonly IWorkflowExecutionEventSink _eventSink;
    private readonly TabularExecutionLimits _limits;
    private readonly TimeProvider _timeProvider;
    private readonly WorkflowGraphSerializer _graphSerializer;
    private readonly INodeExecutionTimeoutProvider _timeoutProvider;
    private readonly WorkflowExecutionTimeoutOptions _timeoutOptions;
    private readonly IWorkflowVariableResolver _variableResolver;

    public BoundedWorkflowExecutionEngine(
        WorkflowExecutionPlanner planner,
        WorkflowNodeExecutorRegistry executors,
        IWorkflowExecutionPreflightValidator preflightValidator,
        ITabularDataSetStore dataSetStore,
        INodeRetryPolicyProvider retryPolicies,
        IWorkflowRetryScheduler retryScheduler,
        IExecutionErrorClassifier errorClassifier,
        IWorkflowExecutionEventSink eventSink,
        TabularExecutionLimits? limits = null,
        TimeProvider? timeProvider = null,
        WorkflowGraphSerializer? graphSerializer = null,
        INodeExecutionTimeoutProvider? timeoutProvider = null,
        WorkflowExecutionTimeoutOptions? timeoutOptions = null,
        IWorkflowVariableResolver? variableResolver = null)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _executors = executors ?? throw new ArgumentNullException(nameof(executors));
        _preflightValidator = preflightValidator
            ?? throw new ArgumentNullException(nameof(preflightValidator));
        _dataSetStore = dataSetStore ?? throw new ArgumentNullException(nameof(dataSetStore));
        _retryPolicies = retryPolicies ?? throw new ArgumentNullException(nameof(retryPolicies));
        _retryScheduler = retryScheduler ?? throw new ArgumentNullException(nameof(retryScheduler));
        _errorClassifier = errorClassifier ?? throw new ArgumentNullException(nameof(errorClassifier));
        _eventSink = eventSink ?? throw new ArgumentNullException(nameof(eventSink));
        _limits = limits ?? new TabularExecutionLimits();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _graphSerializer = graphSerializer ?? new WorkflowGraphSerializer();
        _timeoutOptions = timeoutOptions ?? new WorkflowExecutionTimeoutOptions();
        _timeoutProvider = timeoutProvider ?? new DefaultNodeExecutionTimeoutProvider(_timeoutOptions);
        _variableResolver = variableResolver
            ?? new WorkflowVariableResolver(new UnavailableWorkflowDatabaseVariableProvider());
    }

    public ValueTask<WorkflowExecutionResult> ExecuteSerializedAsync(
        Guid workflowRunId,
        string graphJson,
        CancellationToken cancellationToken)
    {
        if (workflowRunId == Guid.Empty)
        {
            throw new ArgumentException("A workflow run id is required.", nameof(workflowRunId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(graphJson);

        try
        {
            return ExecuteAsync(
                workflowRunId,
                _graphSerializer.Deserialize(graphJson),
                cancellationToken);
        }
        catch (WorkflowGraphSerializationException)
        {
            return ValueTask.FromResult(new WorkflowExecutionResult(
                workflowRunId,
                WorkflowExecutionStatus.Failed,
                [],
                new ExecutionError(
                    "workflow.graph.serialization.invalid",
                    ExecutionErrorCategory.Validation,
                    "The immutable workflow graph document is malformed or uses an unsupported schema.",
                    isRetryable: false)));
        }
    }

    public async ValueTask<WorkflowExecutionResult> ExecuteAsync(
        Guid workflowRunId,
        WorkflowGraph graph,
        CancellationToken cancellationToken)
    {
        if (workflowRunId == Guid.Empty)
        {
            throw new ArgumentException("A workflow run id is required.", nameof(workflowRunId));
        }

        ArgumentNullException.ThrowIfNull(graph);

        WorkflowExecutionPlan plan;
        try
        {
            plan = _planner.CreatePlan(graph);
        }
        catch (WorkflowPlanException)
        {
            return new WorkflowExecutionResult(
                workflowRunId,
                WorkflowExecutionStatus.Failed,
                [],
                new ExecutionError(
                    "workflow.graph.invalid",
                    ExecutionErrorCategory.Validation,
                    "The immutable workflow graph failed execution-boundary validation.",
                    isRetryable: false));
        }

        var preflightError = ValidatePreflight(plan);
        if (preflightError is null)
        {
            try
            {
                preflightError = await _preflightValidator.ValidateAsync(
                    graph,
                    _limits,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new WorkflowExecutionResult(
                    workflowRunId,
                    WorkflowExecutionStatus.Cancelled,
                    plan.Nodes.Select(static node => new WorkflowNodeExecutionOutcome(
                        node.Node.Id,
                        WorkflowNodeExecutionStatus.Cancelled,
                        0,
                        new WorkflowNodeProgress(),
                        ReasonCode: "execution.preflight.cancelled")).ToArray(),
                    new ExecutionError(
                        "execution.cancelled",
                        ExecutionErrorCategory.Cancelled,
                        "Execution was cancelled during preflight validation.",
                        isRetryable: false));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                preflightError = _errorClassifier.Classify(
                    exception,
                    cancellationRequested: false);
            }
        }

        if (preflightError is not null)
        {
            foreach (var node in plan.Nodes)
            {
                await WriteEventAsync(
                    new WorkflowExecutionEvent(
                        workflowRunId,
                        WorkflowExecutionEventKind.NodeSkipped,
                        GetUtcNow(),
                        node.Node.Id,
                        ReasonCode: "execution.preflight.failed"),
                    CancellationToken.None).ConfigureAwait(false);
            }

            return new WorkflowExecutionResult(
                workflowRunId,
                WorkflowExecutionStatus.Failed,
                plan.Nodes.Select(static node => new WorkflowNodeExecutionOutcome(
                    node.Node.Id,
                    WorkflowNodeExecutionStatus.Skipped,
                    0,
                    new WorkflowNodeProgress(),
                    ReasonCode: "execution.preflight.failed")).ToArray(),
                preflightError);
        }

        var outcomes = new Dictionary<string, WorkflowNodeExecutionOutcome>(
            StringComparer.OrdinalIgnoreCase);
        var outputs = new Dictionary<string, ITabularDataSet>(StringComparer.OrdinalIgnoreCase);
        var remainingConsumers = plan.Nodes.ToDictionary(
            static node => node.Node.Id,
            static node => node.ChildNodeIds.Count,
            StringComparer.OrdinalIgnoreCase);
        var resourceBudget = new WorkflowResourceBudget(_limits);
        ExecutionError? engineError = null;
        var cancellationObserved = false;
        using var runDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        runDeadline.CancelAfter(_timeoutOptions.MaximumRunDuration);
        var executionToken = runDeadline.Token;
        var runStartedAtUtc = GetUtcNow();

        try
        {
            await WriteEventAsync(
                new WorkflowExecutionEvent(
                    workflowRunId,
                    WorkflowExecutionEventKind.ExecutionStarted,
                    runStartedAtUtc),
                executionToken).ConfigureAwait(false);
            var variables = await _variableResolver.ResolveAsync(
                workflowRunId,
                runStartedAtUtc,
                graph,
                executionToken).ConfigureAwait(false);

            foreach (var layer in plan.Layers)
            {
                foreach (var plannedNode in layer)
                {
                    if (executionToken.IsCancellationRequested
                        && outcomes.Count < plan.Nodes.Count)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            cancellationObserved = true;
                            await MarkRemainingCancelledAsync(plan, outcomes, workflowRunId)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            engineError = RunTimeoutError();
                            MarkRemainingCancelledOrSkipped(
                                plan,
                                outcomes,
                                cancellationRequested: false,
                                reasonCode: "execution.run.timeout");
                        }

                        break;
                    }

                    if (plannedNode.ParentNodeIds.Any(parentId =>
                            outcomes[parentId].Status != WorkflowNodeExecutionStatus.Succeeded))
                    {
                        var skipped = new WorkflowNodeExecutionOutcome(
                            plannedNode.Node.Id,
                            WorkflowNodeExecutionStatus.Skipped,
                            0,
                            new WorkflowNodeProgress(),
                            ReasonCode: "dependency.not_succeeded");
                        outcomes.Add(plannedNode.Node.Id, skipped);
                        await WriteEventAsync(
                            new WorkflowExecutionEvent(
                                workflowRunId,
                                WorkflowExecutionEventKind.NodeSkipped,
                                GetUtcNow(),
                                plannedNode.Node.Id,
                                ReasonCode: skipped.ReasonCode),
                            executionToken).ConfigureAwait(false);
                        await ReleaseConsumedInputsAsync(
                            plannedNode,
                            remainingConsumers,
                            outputs,
                            resourceBudget).ConfigureAwait(false);
                        continue;
                    }

                    var inputs = plannedNode.ParentNodeIds
                        .Select(parentId => new WorkflowNodeInput(parentId, outputs[parentId]))
                        .ToArray();
                    var executor = GetExecutor(plannedNode.Node.Kind);
                    var execution = await ExecuteNodeAsync(
                        workflowRunId,
                        plannedNode.Node,
                        executor,
                        inputs,
                        resourceBudget,
                        variables,
                        graph.Functions,
                        executionToken,
                        cancellationToken,
                        () => runDeadline.IsCancellationRequested
                            && !cancellationToken.IsCancellationRequested).ConfigureAwait(false);
                    outcomes.Add(plannedNode.Node.Id, execution.Outcome);
                    if (execution.Output is not null)
                    {
                        outputs.Add(plannedNode.Node.Id, execution.Output);
                    }

                    await ReleaseConsumedInputsAsync(
                        plannedNode,
                        remainingConsumers,
                        outputs,
                        resourceBudget).ConfigureAwait(false);
                    await ReleaseOutputWithoutConsumersAsync(
                        plannedNode,
                        outputs,
                        resourceBudget).ConfigureAwait(false);

                    if (execution.Outcome.Status == WorkflowNodeExecutionStatus.Cancelled)
                    {
                        cancellationObserved = true;
                    }

                    if (executionToken.IsCancellationRequested
                        && outcomes.Count < plan.Nodes.Count)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            cancellationObserved = true;
                            await MarkRemainingCancelledAsync(plan, outcomes, workflowRunId)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            engineError ??= RunTimeoutError();
                            MarkRemainingCancelledOrSkipped(
                                plan,
                                outcomes,
                                cancellationRequested: false,
                                reasonCode: "execution.run.timeout");
                        }

                        break;
                    }
                }

                if (executionToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var runTimedOut = runDeadline.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested;
            cancellationObserved |= cancellationToken.IsCancellationRequested;
            engineError = runTimedOut
                ? RunTimeoutError()
                : _errorClassifier.Classify(exception, cancellationToken.IsCancellationRequested);
            MarkRemainingCancelledOrSkipped(
                plan,
                outcomes,
                cancellationToken.IsCancellationRequested,
                runTimedOut ? "execution.run.timeout" : "execution.infrastructure.failed");
        }

        var cleanupError = await DisposeOutputsAsync(outputs.Values).ConfigureAwait(false);
        engineError ??= cleanupError;

        var orderedOutcomes = plan.Nodes
            .Select(node => outcomes.TryGetValue(node.Node.Id, out var outcome)
                ? outcome
                : new WorkflowNodeExecutionOutcome(
                    node.Node.Id,
                    WorkflowNodeExecutionStatus.Skipped,
                    0,
                    new WorkflowNodeProgress(),
                    ReasonCode: "execution.not_started"))
            .ToArray();
        var status = DetermineStatus(orderedOutcomes, engineError, cancellationObserved);
        var primaryError = engineError
            ?? orderedOutcomes.FirstOrDefault(static outcome => outcome.Error is not null)?.Error;

        try
        {
            await WriteEventAsync(
                new WorkflowExecutionEvent(
                    workflowRunId,
                    status switch
                    {
                        WorkflowExecutionStatus.Succeeded => WorkflowExecutionEventKind.ExecutionSucceeded,
                        WorkflowExecutionStatus.Cancelled => WorkflowExecutionEventKind.ExecutionCancelled,
                        _ => WorkflowExecutionEventKind.ExecutionFailed
                    },
                    GetUtcNow(),
                    Error: primaryError),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new WorkflowExecutionResult(
                workflowRunId,
                WorkflowExecutionStatus.Failed,
                orderedOutcomes,
                _errorClassifier.Classify(exception, cancellationRequested: false));
        }

        return new WorkflowExecutionResult(workflowRunId, status, orderedOutcomes, primaryError);
    }

    private ExecutionError? ValidatePreflight(WorkflowExecutionPlan plan)
    {
        foreach (var plannedNode in plan.Nodes)
        {
            if (GatedNodeKinds.Contains(plannedNode.Node.Kind))
            {
                return ConfigurationError(
                    "executor.kind.gated",
                    $"Execution for node kind '{plannedNode.Node.Kind}' is disabled by policy.");
            }

            if (!_executors.TryGet(plannedNode.Node.Kind, out var executor) || executor is null)
            {
                return ConfigurationError(
                    "executor.missing",
                    $"No executor is registered for node kind '{plannedNode.Node.Kind}'.");
            }

            var descriptor = executor.Descriptor;
            if (descriptor.NodeKind != plannedNode.Node.Kind)
            {
                return ConfigurationError(
                    "executor.kind.mismatch",
                    "A workflow node executor registration is inconsistent.");
            }

            if (plannedNode.ParentNodeIds.Count > _limits.MaximumInputsPerNode)
            {
                return new ExecutionError(
                    "executor.inputs.limit",
                    ExecutionErrorCategory.ResourceLimit,
                    $"A node exceeds the configured {_limits.MaximumInputsPerNode}-input limit.",
                    isRetryable: false);
            }

            if ((plannedNode.ParentNodeIds.Count > 0) != descriptor.ConsumesInput)
            {
                return ConfigurationError(
                    "executor.input.capability",
                    "A node's graph inputs do not match its executor capabilities.");
            }

            if (plannedNode.ChildNodeIds.Count > 0 && !descriptor.ProducesOutput)
            {
                return ConfigurationError(
                    "executor.output.capability",
                    "A node with downstream dependencies must produce tabular output.");
            }

            if (!MatchesExpectedRole(plannedNode.Node.Kind, descriptor.Role))
            {
                return ConfigurationError(
                    "executor.role.mismatch",
                    "A node executor is registered with the wrong execution role.");
            }
        }

        return null;
    }

    private async ValueTask<NodeExecution> ExecuteNodeAsync(
        Guid workflowRunId,
        WorkflowNode node,
        IWorkflowNodeExecutor executor,
        IReadOnlyList<WorkflowNodeInput> inputs,
        WorkflowResourceBudget resourceBudget,
        IReadOnlyDictionary<string, TabularValue> variables,
        IReadOnlyList<WorkflowFunction> functions,
        CancellationToken executionToken,
        CancellationToken callerCancellationToken,
        Func<bool> runTimeoutObserved)
    {
        var retryPolicy = _retryPolicies.GetPolicy(node, executor.Descriptor)
            ?? throw new InvalidOperationException("The retry policy provider returned null.");
        var totalProgress = new WorkflowNodeProgress();

        for (var attempt = 1; attempt <= retryPolicy.MaximumAttempts; attempt++)
        {
            var attemptTimeout = WorkflowExecutionTimeoutOptions.Validate(
                _timeoutProvider.GetAttemptTimeout(node, executor.Descriptor),
                "attemptTimeout");
            using var attemptDeadline = CancellationTokenSource.CreateLinkedTokenSource(executionToken);
            attemptDeadline.CancelAfter(attemptTimeout);
            var attemptToken = attemptDeadline.Token;
            ITabularDataSet? materializedOutput = null;
            var outputBudgetAcquired = false;
            var reporter = new EventProgressReporter(
                workflowRunId,
                node.Id,
                attempt,
                WriteEventAsync,
                _timeProvider);
            await WriteEventAsync(
                new WorkflowExecutionEvent(
                    workflowRunId,
                    WorkflowExecutionEventKind.NodeStarted,
                    GetUtcNow(),
                    node.Id,
                    attempt),
                executionToken).ConfigureAwait(false);

            try
            {
                attemptToken.ThrowIfCancellationRequested();
                var contextInputs = inputs
                    .Select(static input => new WorkflowNodeInput(
                        input.SourceNodeId,
                        new NonOwningTabularDataSet(input.DataSet)))
                    .ToArray();
                var context = new WorkflowNodeExecutionContext(
                    workflowRunId,
                    node,
                    attempt,
                    contextInputs,
                    _limits,
                    reporter,
                    variables,
                    functions);
                var result = await executor.ExecuteAsync(context, attemptToken)
                    .AsTask()
                    .WaitAsync(attemptToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The node executor returned null.");
                attemptToken.ThrowIfCancellationRequested();

                if (executor.Descriptor.ProducesOutput)
                {
                    if (result.Output is null)
                    {
                        throw new TabularContractException(
                            "executor.output.required",
                            "The node executor did not return its required tabular output.");
                    }

                    materializedOutput = await _dataSetStore.MaterializeAsync(
                        result.Output.Schema,
                        result.Output.Batches,
                        _limits,
                        attemptToken).ConfigureAwait(false);
                    attemptToken.ThrowIfCancellationRequested();
                    if (!resourceBudget.TryAcquire(materializedOutput))
                    {
                        await materializedOutput.DisposeAsync().ConfigureAwait(false);
                        materializedOutput = null;
                        throw new TabularLimitExceededException(
                            "tabular.workflow.retained.limit",
                            "Materialized workflow data exceeds the configured run-wide retained-data limit.");
                    }

                    outputBudgetAcquired = true;
                }
                else if (result.Output is not null)
                {
                    throw new TabularContractException(
                        "executor.output.unexpected",
                        "The node executor returned tabular output it did not declare.");
                }

                totalProgress = totalProgress.Add(reporter.Total);
                var succeeded = new WorkflowNodeExecutionOutcome(
                    node.Id,
                    WorkflowNodeExecutionStatus.Succeeded,
                    attempt,
                    totalProgress);
                await WriteEventAsync(
                    new WorkflowExecutionEvent(
                        workflowRunId,
                        WorkflowExecutionEventKind.NodeSucceeded,
                        GetUtcNow(),
                        node.Id,
                        attempt,
                        reporter.Total),
                    CancellationToken.None).ConfigureAwait(false);
                return new NodeExecution(succeeded, materializedOutput);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                if (materializedOutput is not null)
                {
                    try
                    {
                        await materializedOutput.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception cleanupException) when (cleanupException is not OutOfMemoryException)
                    {
                        exception = new AggregateException(exception, cleanupException);
                    }

                    if (outputBudgetAcquired)
                    {
                        resourceBudget.Release(materializedOutput);
                    }
                }

                totalProgress = totalProgress.Add(reporter.Total);
                var runTimedOut = runTimeoutObserved();
                var attemptTimedOut = attemptDeadline.IsCancellationRequested
                    && !executionToken.IsCancellationRequested;
                var error = runTimedOut
                    ? RunTimeoutError()
                    : attemptTimedOut
                        ? NodeTimeoutError()
                        : _errorClassifier.Classify(
                            exception,
                            callerCancellationToken.IsCancellationRequested);
                if (error.Category == ExecutionErrorCategory.Cancelled)
                {
                    var cancelled = new WorkflowNodeExecutionOutcome(
                        node.Id,
                        WorkflowNodeExecutionStatus.Cancelled,
                        attempt,
                        totalProgress,
                        error);
                    await WriteEventAsync(
                        new WorkflowExecutionEvent(
                            workflowRunId,
                            WorkflowExecutionEventKind.NodeCancelled,
                            GetUtcNow(),
                            node.Id,
                            attempt,
                            reporter.Total,
                            error),
                        CancellationToken.None).ConfigureAwait(false);
                    return new NodeExecution(cancelled, null);
                }

                if (!runTimedOut
                    && CanRetry(executor.Descriptor, retryPolicy, error, attempt))
                {
                    var delay = _retryScheduler.GetDelay(retryPolicy, attempt);
                    await WriteEventAsync(
                        new WorkflowExecutionEvent(
                            workflowRunId,
                            WorkflowExecutionEventKind.NodeRetryScheduled,
                            GetUtcNow(),
                            node.Id,
                            attempt,
                            reporter.Total,
                            error,
                            delay),
                        executionToken).ConfigureAwait(false);

                    try
                    {
                        await _retryScheduler.DelayAsync(delay, executionToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (executionToken.IsCancellationRequested)
                    {
                        var cancellationError = runTimeoutObserved()
                            ? RunTimeoutError()
                            : _errorClassifier.Classify(
                                new OperationCanceledException(callerCancellationToken),
                                callerCancellationToken.IsCancellationRequested);
                        var cancelledByCaller = cancellationError.Category
                            == ExecutionErrorCategory.Cancelled;
                        var stopped = new WorkflowNodeExecutionOutcome(
                            node.Id,
                            cancelledByCaller
                                ? WorkflowNodeExecutionStatus.Cancelled
                                : WorkflowNodeExecutionStatus.Failed,
                            attempt,
                            totalProgress,
                            cancellationError);
                        await WriteEventAsync(
                            new WorkflowExecutionEvent(
                                workflowRunId,
                                cancelledByCaller
                                    ? WorkflowExecutionEventKind.NodeCancelled
                                    : WorkflowExecutionEventKind.NodeFailed,
                                GetUtcNow(),
                                node.Id,
                                attempt,
                                reporter.Total,
                                cancellationError),
                            CancellationToken.None).ConfigureAwait(false);
                        return new NodeExecution(stopped, null);
                    }

                    continue;
                }

                var failed = new WorkflowNodeExecutionOutcome(
                    node.Id,
                    WorkflowNodeExecutionStatus.Failed,
                    attempt,
                    totalProgress,
                    error);
                await WriteEventAsync(
                    new WorkflowExecutionEvent(
                        workflowRunId,
                        WorkflowExecutionEventKind.NodeFailed,
                        GetUtcNow(),
                        node.Id,
                        attempt,
                        reporter.Total,
                        error),
                    CancellationToken.None).ConfigureAwait(false);
                return new NodeExecution(failed, null);
            }
        }

        throw new InvalidOperationException("The bounded node-attempt loop ended unexpectedly.");
    }

    private static bool CanRetry(
        WorkflowNodeExecutorDescriptor descriptor,
        NodeRetryPolicy policy,
        ExecutionError error,
        int completedAttempt)
    {
        if (!error.IsRetryable
            || !IsTransient(error.Category)
            || completedAttempt >= policy.MaximumAttempts
            || descriptor.RetrySafety == WorkflowNodeRetrySafety.Never)
        {
            return false;
        }

        return !descriptor.HasExternalSideEffect
            || descriptor.RetrySafety == WorkflowNodeRetrySafety.IdempotentSideEffect;
    }

    private static bool IsTransient(ExecutionErrorCategory category) => category is
        ExecutionErrorCategory.Connectivity
        or ExecutionErrorCategory.Timeout
        or ExecutionErrorCategory.RateLimited;

    private IWorkflowNodeExecutor GetExecutor(WorkflowNodeKind kind)
    {
        if (!_executors.TryGet(kind, out var executor) || executor is null)
        {
            throw new InvalidOperationException($"No executor is registered for '{kind}'.");
        }

        return executor;
    }

    private async ValueTask<ExecutionError?> DisposeOutputsAsync(
        IEnumerable<ITabularDataSet> outputs)
    {
        ExecutionError? firstError = null;
        foreach (var output in outputs.Reverse())
        {
            try
            {
                await output.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                firstError ??= _errorClassifier.Classify(exception, cancellationRequested: false);
            }
        }

        return firstError;
    }

    private static async ValueTask ReleaseConsumedInputsAsync(
        WorkflowExecutionPlanNode plannedNode,
        IDictionary<string, int> remainingConsumers,
        IDictionary<string, ITabularDataSet> outputs,
        WorkflowResourceBudget resourceBudget)
    {
        foreach (var parentNodeId in plannedNode.ParentNodeIds)
        {
            remainingConsumers[parentNodeId]--;
            if (remainingConsumers[parentNodeId] == 0
                && outputs.Remove(parentNodeId, out var output))
            {
                resourceBudget.Release(output);
                await output.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask ReleaseOutputWithoutConsumersAsync(
        WorkflowExecutionPlanNode plannedNode,
        IDictionary<string, ITabularDataSet> outputs,
        WorkflowResourceBudget resourceBudget)
    {
        if (plannedNode.ChildNodeIds.Count == 0
            && outputs.Remove(plannedNode.Node.Id, out var output))
        {
            resourceBudget.Release(output);
            await output.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask MarkRemainingCancelledAsync(
        WorkflowExecutionPlan plan,
        IDictionary<string, WorkflowNodeExecutionOutcome> outcomes,
        Guid workflowRunId)
    {
        var error = new ExecutionError(
            "execution.cancelled",
            ExecutionErrorCategory.Cancelled,
            "Execution was cancelled.",
            isRetryable: false);
        foreach (var node in plan.Nodes)
        {
            if (!outcomes.ContainsKey(node.Node.Id))
            {
                outcomes.Add(
                    node.Node.Id,
                    new WorkflowNodeExecutionOutcome(
                        node.Node.Id,
                        WorkflowNodeExecutionStatus.Cancelled,
                        0,
                        new WorkflowNodeProgress(),
                        error));
                await WriteEventAsync(
                    new WorkflowExecutionEvent(
                        workflowRunId,
                        WorkflowExecutionEventKind.NodeCancelled,
                        GetUtcNow(),
                        node.Node.Id,
                        Error: error,
                        ReasonCode: "execution.cancelled"),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static void MarkRemainingCancelledOrSkipped(
        WorkflowExecutionPlan plan,
        IDictionary<string, WorkflowNodeExecutionOutcome> outcomes,
        bool cancellationRequested,
        string reasonCode)
    {
        foreach (var node in plan.Nodes)
        {
            if (outcomes.ContainsKey(node.Node.Id))
            {
                continue;
            }

            outcomes.Add(
                node.Node.Id,
                new WorkflowNodeExecutionOutcome(
                    node.Node.Id,
                    cancellationRequested
                        ? WorkflowNodeExecutionStatus.Cancelled
                        : WorkflowNodeExecutionStatus.Skipped,
                    0,
                    new WorkflowNodeProgress(),
                    ReasonCode: cancellationRequested
                        ? "execution.cancelled"
                        : reasonCode));
        }
    }

    private static WorkflowExecutionStatus DetermineStatus(
        IReadOnlyList<WorkflowNodeExecutionOutcome> outcomes,
        ExecutionError? engineError,
        bool cancellationObserved)
    {
        if (cancellationObserved
            || engineError?.Category == ExecutionErrorCategory.Cancelled
            || outcomes.Any(static outcome => outcome.Status == WorkflowNodeExecutionStatus.Cancelled))
        {
            return WorkflowExecutionStatus.Cancelled;
        }

        if (engineError is not null
            || outcomes.Any(static outcome => outcome.Status == WorkflowNodeExecutionStatus.Failed))
        {
            return WorkflowExecutionStatus.Failed;
        }

        return WorkflowExecutionStatus.Succeeded;
    }

    private static bool MatchesExpectedRole(WorkflowNodeKind kind, WorkflowNodeRole role) => kind switch
    {
        WorkflowNodeKind.SqlSource or WorkflowNodeKind.MySqlSource
            or WorkflowNodeKind.PostgreSqlSource or WorkflowNodeKind.OracleSource
            or WorkflowNodeKind.FtpSource or WorkflowNodeKind.CsvSource
            or WorkflowNodeKind.ExcelSource =>
            role == WorkflowNodeRole.Source,
        WorkflowNodeKind.SelectColumns or WorkflowNodeKind.FilterRows or WorkflowNodeKind.Join =>
            role == WorkflowNodeRole.Transform,
        WorkflowNodeKind.SqlTarget or WorkflowNodeKind.MySqlTarget
            or WorkflowNodeKind.PostgreSqlTarget or WorkflowNodeKind.OracleTarget
            or WorkflowNodeKind.FtpTarget or WorkflowNodeKind.CsvTarget
            or WorkflowNodeKind.ExcelTarget =>
            role == WorkflowNodeRole.Target,
        WorkflowNodeKind.EmailAlert => role == WorkflowNodeRole.Action,
        _ => false
    };

    private static ExecutionError ConfigurationError(string code, string summary) => new(
        code,
        ExecutionErrorCategory.Configuration,
        summary,
        isRetryable: false);

    private static ExecutionError NodeTimeoutError() => new(
        "execution.node.timeout",
        ExecutionErrorCategory.Timeout,
        "The node attempt exceeded its configured time limit.",
        isRetryable: true);

    private static ExecutionError RunTimeoutError() => new(
        "execution.run.timeout",
        ExecutionErrorCategory.Timeout,
        "The workflow run exceeded its configured time limit.",
        isRetryable: false);

    private DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow();

    private async ValueTask WriteEventAsync(
        WorkflowExecutionEvent executionEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            await _eventSink.WriteAsync(executionEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new WorkflowExecutionEventSinkException(exception);
        }
    }

    private sealed record NodeExecution(
        WorkflowNodeExecutionOutcome Outcome,
        ITabularDataSet? Output);

    private sealed class WorkflowResourceBudget
    {
        private readonly long _maximumRows;
        private readonly long _maximumBytes;
        private readonly object _gate = new();
        private long _retainedRows;
        private long _retainedBytes;

        public WorkflowResourceBudget(TabularExecutionLimits limits)
        {
            _maximumRows = limits.MaximumRetainedRowsPerWorkflow;
            _maximumBytes = limits.MaximumRetainedBytesPerWorkflow;
        }

        public bool TryAcquire(ITabularDataSet dataSet)
        {
            lock (_gate)
            {
                if (dataSet.RowCount > _maximumRows - _retainedRows
                    || dataSet.ByteCount > _maximumBytes - _retainedBytes)
                {
                    return false;
                }

                _retainedRows += dataSet.RowCount;
                _retainedBytes += dataSet.ByteCount;
                return true;
            }
        }

        public void Release(ITabularDataSet dataSet)
        {
            lock (_gate)
            {
                _retainedRows -= dataSet.RowCount;
                _retainedBytes -= dataSet.ByteCount;
            }
        }
    }

    private sealed class NonOwningTabularDataSet : ITabularDataSet
    {
        private readonly ITabularDataSet _inner;

        public NonOwningTabularDataSet(ITabularDataSet inner)
        {
            _inner = inner;
        }

        public TabularSchema Schema => _inner.Schema;

        public long RowCount => _inner.RowCount;

        public long ByteCount => _inner.ByteCount;

        public IAsyncEnumerable<TabularBatch> ReadBatchesAsync(
            CancellationToken cancellationToken = default) =>
            _inner.ReadBatchesAsync(cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EventProgressReporter : IWorkflowNodeProgressReporter
    {
        private const int MaximumEventsPerAttempt = 1_000;
        private static readonly TimeSpan MinimumEventInterval = TimeSpan.FromSeconds(1);

        private readonly Guid _workflowRunId;
        private readonly string _nodeId;
        private readonly int _attempt;
        private readonly Func<WorkflowExecutionEvent, CancellationToken, ValueTask> _writeEvent;
        private readonly TimeProvider _timeProvider;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private WorkflowNodeProgress _total = new();
        private WorkflowNodeProgress _lastEmitted = new();
        private DateTimeOffset? _lastEmittedAtUtc;
        private int _eventCount;

        public EventProgressReporter(
            Guid workflowRunId,
            string nodeId,
            int attempt,
            Func<WorkflowExecutionEvent, CancellationToken, ValueTask> writeEvent,
            TimeProvider timeProvider)
        {
            _workflowRunId = workflowRunId;
            _nodeId = nodeId;
            _attempt = attempt;
            _writeEvent = writeEvent;
            _timeProvider = timeProvider;
        }

        public WorkflowNodeProgress Total => _total;

        public async ValueTask ReportAsync(
            WorkflowNodeProgress delta,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(delta);
            delta.Validate();
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _total = _total.Add(delta);
                var now = _timeProvider.GetUtcNow();
                if (!ShouldEmit(now))
                {
                    return;
                }

                await _writeEvent(
                    new WorkflowExecutionEvent(
                        _workflowRunId,
                        WorkflowExecutionEventKind.NodeProgressed,
                        now,
                        _nodeId,
                        _attempt,
                        _total),
                    cancellationToken).ConfigureAwait(false);
                _lastEmitted = _total;
                _lastEmittedAtUtc = now;
                _eventCount++;
            }
            finally
            {
                _gate.Release();
            }
        }

        private bool ShouldEmit(DateTimeOffset now)
        {
            if (_eventCount >= MaximumEventsPerAttempt)
            {
                return false;
            }

            if (_lastEmittedAtUtc is null)
            {
                return true;
            }

            return now - _lastEmittedAtUtc >= MinimumEventInterval
                || _total.RowsRead - _lastEmitted.RowsRead >= 1_000
                || _total.RowsWritten - _lastEmitted.RowsWritten >= 1_000
                || _total.BatchesProcessed - _lastEmitted.BatchesProcessed >= 10
                || _total.BytesRead - _lastEmitted.BytesRead >= 1024 * 1024
                || _total.BytesWritten - _lastEmitted.BytesWritten >= 1024 * 1024;
        }
    }
}
