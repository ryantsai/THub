using System.Runtime.CompilerServices;
using THub.Application.Execution;
using THub.Domain.Workflows;

namespace THub.Infrastructure.Execution;

public sealed class DistinctRowsNodeExecutor(
    WorkflowNodeSettingsValidator settingsValidator) : IWorkflowNodeExecutor
{
    public WorkflowNodeExecutorDescriptor Descriptor { get; } =
        WorkflowNodeExecutorDescriptor.Transform(WorkflowNodeKind.DistinctRows);

    public ValueTask<WorkflowNodeExecutionResult> ExecuteAsync(
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = (DistinctRowsNodeSettings)settingsValidator.Parse(context.Node);
        var input = TabularExecutionSupport.RequireSingleInput(context);
        var indexes = settings.Columns is null || settings.Columns.Count == 0
            ? Enumerable.Range(0, input.DataSet.Schema.Columns.Count).ToArray()
            : settings.Columns.Select(column =>
                TabularExecutionSupport.FindColumn(input.DataSet.Schema, column)).ToArray();
        return ValueTask.FromResult(WorkflowNodeExecutionResult.WithOutput(
            input.DataSet.Schema,
            DistinctAsync(
                input.DataSet,
                indexes,
                settings.MaximumKeys,
                context.Progress,
                cancellationToken)));
    }

    private static async IAsyncEnumerable<TabularBatch> DistinctAsync(
        ITabularDataSet input,
        IReadOnlyList<int> indexes,
        int maximumKeys,
        IWorkflowNodeProgressReporter progress,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var keys = new TransformStructuralKeySet();
        await foreach (var batch in input.ReadBatchesAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            await using (batch.ConfigureAwait(false))
            {
                var rows = new List<TabularRow>(batch.Rows.Count);
                foreach (var row in batch.Rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var addResult = keys.TryAdd(
                        row,
                        indexes,
                        maximumKeys,
                        cancellationToken);
                    if (addResult == TransformKeyAddResult.LimitExceeded)
                    {
                        throw new TabularLimitExceededException(
                            "execution.distinct.key.limit",
                            $"Distinct rows exceed the configured {maximumKeys}-key limit.");
                    }
                    if (addResult == TransformKeyAddResult.Added)
                    {
                        rows.Add(row);
                    }
                }

                await progress.ReportAsync(
                    new WorkflowNodeProgress(
                        RowsRead: batch.Rows.Count,
                        BatchesProcessed: 1,
                        BytesRead: batch.EstimatedByteCount),
                    cancellationToken);
                if (rows.Count > 0)
                {
                    yield return new TabularBatch(rows);
                }
            }
        }
    }
}
