using System.Runtime.CompilerServices;
using THub.Application.Execution;
using THub.Domain.Workflows;

namespace THub.Infrastructure.Execution;

public sealed class SortRowsNodeExecutor : IWorkflowNodeExecutor
{
    private readonly WorkflowNodeSettingsValidator settingsValidator;
    private readonly Action<int>? comparisonObserver;

    public SortRowsNodeExecutor(WorkflowNodeSettingsValidator settingsValidator)
        : this(settingsValidator, comparisonObserver: null)
    {
    }

    internal SortRowsNodeExecutor(
        WorkflowNodeSettingsValidator settingsValidator,
        Action<int>? comparisonObserver)
    {
        this.settingsValidator = settingsValidator
            ?? throw new ArgumentNullException(nameof(settingsValidator));
        this.comparisonObserver = comparisonObserver;
    }

    public WorkflowNodeExecutorDescriptor Descriptor { get; } =
        WorkflowNodeExecutorDescriptor.Transform(WorkflowNodeKind.SortRows);

    public ValueTask<WorkflowNodeExecutionResult> ExecuteAsync(
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = (SortRowsNodeSettings)settingsValidator.Parse(context.Node);
        var input = TabularExecutionSupport.RequireSingleInput(context);
        var keys = settings.Keys
            .Select(key => new ResolvedSortKey(
                TabularExecutionSupport.FindColumn(input.DataSet.Schema, key.Column),
                key.Direction,
                key.Nulls))
            .ToArray();
        return ValueTask.FromResult(WorkflowNodeExecutionResult.WithOutput(
            input.DataSet.Schema,
            SortAsync(
                input.DataSet,
                keys,
                settings.MaximumBufferedRows,
                context,
                comparisonObserver,
                cancellationToken)));
    }

    private static async IAsyncEnumerable<TabularBatch> SortAsync(
        ITabularDataSet input,
        IReadOnlyList<ResolvedSortKey> keys,
        int maximumBufferedRows,
        WorkflowNodeExecutionContext context,
        Action<int>? comparisonObserver,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var rows = new List<IndexedRow>();
        long ordinal = 0;
        await foreach (var batch in input.ReadBatchesAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            await using (batch.ConfigureAwait(false))
            {
                foreach (var row in batch.Rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (rows.Count == maximumBufferedRows)
                    {
                        throw new TabularLimitExceededException(
                            "execution.sort.buffer.limit",
                            $"Sort input exceeds the configured {maximumBufferedRows}-row buffer limit.");
                    }

                    rows.Add(new(row, ordinal));
                    ordinal = checked(ordinal + 1);
                }

                await context.Progress.ReportAsync(
                    new WorkflowNodeProgress(
                        RowsRead: batch.Rows.Count,
                        BatchesProcessed: 1,
                        BytesRead: batch.EstimatedByteCount),
                    cancellationToken);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var comparisons = 0;
        try
        {
            rows.Sort((left, right) =>
            {
                var comparison = CompareRows(left, right, keys);
                comparisons++;
                comparisonObserver?.Invoke(comparisons);
                if ((comparisons & 255) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return comparison;
            });
        }
        catch (InvalidOperationException exception)
            when (exception.InnerException is OperationCanceledException cancellationException)
        {
            throw cancellationException;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var sorted = new TabularRow[rows.Count];
        for (var index = 0; index < rows.Count; index++)
        {
            if ((index & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            sorted[index] = rows[index].Row;
        }
        await foreach (var batch in TransformValueSupport.BatchRowsAsync(
                           sorted,
                           context.Limits.MaximumRowsPerBatch,
                           cancellationToken))
        {
            yield return batch;
        }
    }

    private static int CompareRows(
        IndexedRow left,
        IndexedRow right,
        IReadOnlyList<ResolvedSortKey> keys)
    {
        foreach (var key in keys)
        {
            var leftValue = left.Row.Values[key.Index];
            var rightValue = right.Row.Values[key.Index];
            var leftNull = leftValue.Kind == TabularValueKind.Null;
            var rightNull = rightValue.Kind == TabularValueKind.Null;
            if (leftNull || rightNull)
            {
                if (leftNull && rightNull)
                {
                    continue;
                }

                return leftNull == (key.Nulls == SortNullPlacement.First) ? -1 : 1;
            }

            var comparison = TransformValueSupport.Compare(leftValue, rightValue);
            if (comparison != 0)
            {
                return key.Direction == SortDirection.Ascending
                    ? comparison
                    : comparison < 0 ? 1 : -1;
            }
        }

        return left.Ordinal.CompareTo(right.Ordinal);
    }

    private sealed record ResolvedSortKey(
        int Index,
        SortDirection Direction,
        SortNullPlacement Nulls);

    private sealed record IndexedRow(TabularRow Row, long Ordinal);
}
