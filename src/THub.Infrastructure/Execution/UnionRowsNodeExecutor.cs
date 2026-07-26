using System.Runtime.CompilerServices;
using THub.Application.Execution;
using THub.Application.Workflows;
using THub.Domain.Workflows;

namespace THub.Infrastructure.Execution;

public sealed class UnionRowsNodeExecutor(
    WorkflowNodeSettingsValidator settingsValidator) : IWorkflowNodeExecutor
{
    public WorkflowNodeExecutorDescriptor Descriptor { get; } =
        WorkflowNodeExecutorDescriptor.Transform(WorkflowNodeKind.UnionRows);

    public ValueTask<WorkflowNodeExecutionResult> ExecuteAsync(
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = (UnionRowsNodeSettings)settingsValidator.Parse(context.Node);
        var inputs = ResolveInputs(context.Inputs, settings.InputNodeIds);
        WorkflowUnionSchemaPlan schemaPlan;
        try
        {
            schemaPlan = WorkflowTransformSchemaSemantics.CreateUnionPlan(
                inputs.Select(input => input.DataSet.Schema).ToArray(),
                settings.MatchBy);
        }
        catch (WorkflowTransformSchemaException exception)
        {
            throw SchemaMismatch(exception);
        }
        return ValueTask.FromResult(WorkflowNodeExecutionResult.WithOutput(
            schemaPlan.Schema,
            UnionAsync(
                inputs,
                schemaPlan.Alignments,
                settings.Mode,
                context.Progress,
                cancellationToken)));
    }

    private static IReadOnlyList<WorkflowNodeInput> ResolveInputs(
        IReadOnlyList<WorkflowNodeInput> inputs,
        IReadOnlyList<string> configuredIds)
    {
        if (inputs.Count != configuredIds.Count)
        {
            throw InputMismatch();
        }

        var byId = new Dictionary<string, WorkflowNodeInput>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputs)
        {
            if (!byId.TryAdd(input.SourceNodeId, input))
            {
                throw InputMismatch();
            }
        }

        var ordered = new WorkflowNodeInput[configuredIds.Count];
        for (var index = 0; index < configuredIds.Count; index++)
        {
            if (!byId.TryGetValue(configuredIds[index], out var input))
            {
                throw InputMismatch();
            }

            ordered[index] = input;
        }

        return ordered;
    }

    private static async IAsyncEnumerable<TabularBatch> UnionAsync(
        IReadOnlyList<WorkflowNodeInput> inputs,
        IReadOnlyList<IReadOnlyList<int>> alignments,
        UnionRowMode mode,
        IWorkflowNodeProgressReporter progress,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        TransformStructuralKeySet? keys = mode == UnionRowMode.Distinct
            ? new TransformStructuralKeySet()
            : null;
        for (var inputIndex = 0; inputIndex < inputs.Count; inputIndex++)
        {
            var dataSet = inputs[inputIndex].DataSet;
            var alignment = alignments[inputIndex];
            await foreach (var batch in dataSet.ReadBatchesAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                await using (batch.ConfigureAwait(false))
                {
                    var output = new List<TabularRow>(batch.Rows.Count);
                    foreach (var row in batch.Rows)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (keys is null)
                        {
                            output.Add(new TabularRow(
                                alignment.Select(index => row.Values[index])));
                            continue;
                        }

                        var addResult = keys.TryAdd(
                            row,
                            alignment,
                            int.MaxValue,
                            cancellationToken,
                            out var ownedKey);
                        if (addResult == TransformKeyAddResult.Added)
                        {
                            output.Add(new TabularRow(ownedKey!.Values));
                        }
                    }

                    await progress.ReportAsync(
                        new WorkflowNodeProgress(
                            RowsRead: batch.Rows.Count,
                            BatchesProcessed: 1,
                            BytesRead: batch.EstimatedByteCount),
                        cancellationToken);
                    if (output.Count > 0)
                    {
                        yield return new TabularBatch(output);
                    }
                }
            }
        }
    }

    private static WorkflowNodeExecutionException InputMismatch() =>
        ExecutionFailure.Configuration(
            "execution.union.input",
            "Union inputs must match the configured input node ids exactly.");

    private static WorkflowNodeExecutionException SchemaMismatch(
        Exception? exception = null) =>
        ExecutionFailure.Configuration(
            "execution.union.schema",
            "Union input schemas are not compatible with the configured match mode.",
            exception);
}
