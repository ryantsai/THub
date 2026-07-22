using System.Text.Json;
using THub.Domain.Runs;
using THub.Domain.Workflows;

namespace THub.Application.Execution;

[Flags]
public enum WorkflowNodeCapabilities
{
    None = 0,
    ConsumesTabularInput = 1,
    ProducesTabularOutput = 2,
    ExternalSideEffect = 4
}

public enum WorkflowNodeRole
{
    Source,
    Transform,
    Target,
    Action
}

public enum WorkflowNodeRetrySafety
{
    Never,
    ReadOnly,
    IdempotentSideEffect
}

public sealed record WorkflowNodeExecutorDescriptor
{
    public WorkflowNodeExecutorDescriptor(
        WorkflowNodeKind nodeKind,
        WorkflowNodeRole role,
        WorkflowNodeCapabilities capabilities,
        WorkflowNodeRetrySafety retrySafety)
    {
        if (!Enum.IsDefined(nodeKind))
        {
            throw new ArgumentOutOfRangeException(nameof(nodeKind));
        }

        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        if (!Enum.IsDefined(retrySafety))
        {
            throw new ArgumentOutOfRangeException(nameof(retrySafety));
        }

        ValidateCombination(role, capabilities, retrySafety);
        NodeKind = nodeKind;
        Role = role;
        Capabilities = capabilities;
        RetrySafety = retrySafety;
    }

    public WorkflowNodeKind NodeKind { get; }

    public WorkflowNodeRole Role { get; }

    public WorkflowNodeCapabilities Capabilities { get; }

    public WorkflowNodeRetrySafety RetrySafety { get; }

    public bool ConsumesInput => Capabilities.HasFlag(WorkflowNodeCapabilities.ConsumesTabularInput);

    public bool ProducesOutput => Capabilities.HasFlag(WorkflowNodeCapabilities.ProducesTabularOutput);

    public bool HasExternalSideEffect => Capabilities.HasFlag(WorkflowNodeCapabilities.ExternalSideEffect);

    public static WorkflowNodeExecutorDescriptor Source(WorkflowNodeKind kind) => new(
        kind,
        WorkflowNodeRole.Source,
        WorkflowNodeCapabilities.ProducesTabularOutput,
        WorkflowNodeRetrySafety.ReadOnly);

    public static WorkflowNodeExecutorDescriptor Transform(WorkflowNodeKind kind) => new(
        kind,
        WorkflowNodeRole.Transform,
        WorkflowNodeCapabilities.ConsumesTabularInput
            | WorkflowNodeCapabilities.ProducesTabularOutput,
        WorkflowNodeRetrySafety.ReadOnly);

    public static WorkflowNodeExecutorDescriptor Target(
        WorkflowNodeKind kind,
        bool explicitlyIdempotent = false) => new(
            kind,
            WorkflowNodeRole.Target,
            WorkflowNodeCapabilities.ConsumesTabularInput
            | WorkflowNodeCapabilities.ExternalSideEffect,
            explicitlyIdempotent
                ? WorkflowNodeRetrySafety.IdempotentSideEffect
                : WorkflowNodeRetrySafety.Never);

    public static WorkflowNodeExecutorDescriptor Action(
        WorkflowNodeKind kind,
        bool consumesInput = true,
        bool explicitlyIdempotent = false) => new(
            kind,
            WorkflowNodeRole.Action,
            (consumesInput ? WorkflowNodeCapabilities.ConsumesTabularInput : WorkflowNodeCapabilities.None)
                | WorkflowNodeCapabilities.ExternalSideEffect,
            explicitlyIdempotent
                ? WorkflowNodeRetrySafety.IdempotentSideEffect
                : WorkflowNodeRetrySafety.Never);

    private static void ValidateCombination(
        WorkflowNodeRole role,
        WorkflowNodeCapabilities capabilities,
        WorkflowNodeRetrySafety retrySafety)
    {
        const WorkflowNodeCapabilities supportedCapabilities =
            WorkflowNodeCapabilities.ConsumesTabularInput
            | WorkflowNodeCapabilities.ProducesTabularOutput
            | WorkflowNodeCapabilities.ExternalSideEffect;
        if ((capabilities & ~supportedCapabilities) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capabilities),
                capabilities,
                "The executor declares an unsupported capability.");
        }

        var consumes = capabilities.HasFlag(WorkflowNodeCapabilities.ConsumesTabularInput);
        var produces = capabilities.HasFlag(WorkflowNodeCapabilities.ProducesTabularOutput);
        var sideEffect = capabilities.HasFlag(WorkflowNodeCapabilities.ExternalSideEffect);

        if (role == WorkflowNodeRole.Source && (consumes || !produces || sideEffect))
        {
            throw new ArgumentException("A source must only produce tabular output.", nameof(capabilities));
        }

        if (role == WorkflowNodeRole.Transform && (!consumes || !produces || sideEffect))
        {
            throw new ArgumentException(
                "A transform must consume and produce tabular data without an external side effect.",
                nameof(capabilities));
        }

        if (role is WorkflowNodeRole.Target or WorkflowNodeRole.Action && !sideEffect)
        {
            throw new ArgumentException("A target or action must declare its external side effect.", nameof(capabilities));
        }

        if (role == WorkflowNodeRole.Target && (!consumes || produces))
        {
            throw new ArgumentException(
                "A target must consume input and cannot produce output.",
                nameof(capabilities));
        }

        if (role == WorkflowNodeRole.Action && produces)
        {
            throw new ArgumentException("An action cannot produce tabular output.", nameof(capabilities));
        }

        if (sideEffect && retrySafety == WorkflowNodeRetrySafety.ReadOnly)
        {
            throw new ArgumentException(
                "An external side effect cannot declare read-only retry safety.",
                nameof(retrySafety));
        }

        if (!sideEffect && retrySafety == WorkflowNodeRetrySafety.IdempotentSideEffect)
        {
            throw new ArgumentException(
                "Idempotent-side-effect retry safety requires an external side effect.",
                nameof(retrySafety));
        }
    }
}

public interface IWorkflowNodeExecutor
{
    WorkflowNodeExecutorDescriptor Descriptor { get; }

    ValueTask<WorkflowNodeExecutionResult> ExecuteAsync(
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken);
}

public sealed record WorkflowNodeInput
{
    public WorkflowNodeInput(string sourceNodeId, ITabularDataSet dataSet)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceNodeId);
        SourceNodeId = sourceNodeId;
        DataSet = dataSet ?? throw new ArgumentNullException(nameof(dataSet));
    }

    public string SourceNodeId { get; }

    /// <summary>The engine-owned replayable data set. Executors must not dispose it.</summary>
    public ITabularDataSet DataSet { get; }
}

public sealed class WorkflowNodeExecutionContext
{
    public WorkflowNodeExecutionContext(
        Guid workflowRunId,
        WorkflowNode node,
        int attempt,
        IReadOnlyList<WorkflowNodeInput> inputs,
        TabularExecutionLimits limits,
        IWorkflowNodeProgressReporter progress)
    {
        if (workflowRunId == Guid.Empty)
        {
            throw new ArgumentException("A workflow run id is required.", nameof(workflowRunId));
        }

        ArgumentNullException.ThrowIfNull(node);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(progress);

        WorkflowRunId = workflowRunId;
        Node = node;
        Attempt = attempt;
        Inputs = inputs;
        Limits = limits;
        Progress = progress;

        using var settings = JsonDocument.Parse(node.SettingsJson);
        if (settings.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Node settings must be a JSON object.", nameof(node));
        }

        Settings = settings.RootElement.Clone();
    }

    public Guid WorkflowRunId { get; }

    public WorkflowNode Node { get; }

    public int Attempt { get; }

    /// <summary>
    /// Replayable engine-owned inputs bound to stable source-node identities. Executors must not
    /// dispose the underlying data sets.
    /// </summary>
    public IReadOnlyList<WorkflowNodeInput> Inputs { get; }

    public JsonElement Settings { get; }

    public TabularExecutionLimits Limits { get; }

    public IWorkflowNodeProgressReporter Progress { get; }
}

public sealed class WorkflowNodeOutput
{
    public WorkflowNodeOutput(
        TabularSchema schema,
        IAsyncEnumerable<TabularBatch> batches)
    {
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        Batches = batches ?? throw new ArgumentNullException(nameof(batches));
    }

    public TabularSchema Schema { get; }

    public IAsyncEnumerable<TabularBatch> Batches { get; }
}

public sealed class WorkflowNodeExecutionResult
{
    private WorkflowNodeExecutionResult(WorkflowNodeOutput? output)
    {
        Output = output;
    }

    public WorkflowNodeOutput? Output { get; }

    public static WorkflowNodeExecutionResult WithoutOutput { get; } =
        new((WorkflowNodeOutput?)null);

    public static WorkflowNodeExecutionResult WithOutput(
        TabularSchema schema,
        IAsyncEnumerable<TabularBatch> batches) =>
        new(new WorkflowNodeOutput(schema, batches));
}

public sealed class WorkflowNodeExecutionException : Exception
{
    public WorkflowNodeExecutionException(ExecutionError error)
        : base((error ?? throw new ArgumentNullException(nameof(error))).Summary)
    {
        Error = error;
    }

    public WorkflowNodeExecutionException(ExecutionError error, Exception innerException)
        : base((error ?? throw new ArgumentNullException(nameof(error))).Summary, innerException)
    {
        Error = error;
    }

    public ExecutionError Error { get; }
}

public sealed record WorkflowNodeProgress(
    long RowsRead = 0,
    long RowsWritten = 0,
    long BatchesProcessed = 0,
    long BytesRead = 0,
    long BytesWritten = 0)
{
    public WorkflowNodeProgress Validate()
    {
        if (RowsRead < 0 || RowsWritten < 0 || BatchesProcessed < 0 || BytesRead < 0 || BytesWritten < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(WorkflowNodeProgress),
                "Progress deltas cannot be negative.");
        }

        return this;
    }

    internal WorkflowNodeProgress Add(WorkflowNodeProgress delta) => new(
        checked(RowsRead + delta.RowsRead),
        checked(RowsWritten + delta.RowsWritten),
        checked(BatchesProcessed + delta.BatchesProcessed),
        checked(BytesRead + delta.BytesRead),
        checked(BytesWritten + delta.BytesWritten));
}

public interface IWorkflowNodeProgressReporter
{
    ValueTask ReportAsync(WorkflowNodeProgress delta, CancellationToken cancellationToken);
}
