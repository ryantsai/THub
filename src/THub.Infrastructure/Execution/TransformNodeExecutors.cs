using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using THub.Application.Execution;
using THub.Domain.Workflows;

namespace THub.Infrastructure.Execution;

public sealed class SelectColumnsNodeExecutor(
    WorkflowNodeSettingsValidator settingsValidator) : IWorkflowNodeExecutor
{
    public WorkflowNodeExecutorDescriptor Descriptor { get; } =
        WorkflowNodeExecutorDescriptor.Transform(WorkflowNodeKind.SelectColumns);

    public ValueTask<WorkflowNodeExecutionResult> ExecuteAsync(
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = (SelectColumnsNodeSettings)settingsValidator.Parse(context.Node);
        var input = TabularExecutionSupport.RequireSingleInput(context);
        var indexes = settings.Columns
            .Select(column => TabularExecutionSupport.FindColumn(input.DataSet.Schema, column))
            .ToArray();
        var schema = new TabularSchema(indexes.Select(index => input.DataSet.Schema.Columns[index]));
        return ValueTask.FromResult(WorkflowNodeExecutionResult.WithOutput(
            schema,
            ProjectAsync(input.DataSet, indexes, context.Progress, cancellationToken)));
    }

    private static async IAsyncEnumerable<TabularBatch> ProjectAsync(
        ITabularDataSet input,
        IReadOnlyList<int> indexes,
        IWorkflowNodeProgressReporter progress,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var batch in input.ReadBatchesAsync(cancellationToken).ConfigureAwait(false))
        {
            await using (batch.ConfigureAwait(false))
            {
                var rows = batch.Rows
                    .Select(row => new TabularRow(indexes.Select(index => row.Values[index])))
                    .ToArray();
                var output = new TabularBatch(rows);
                await progress.ReportAsync(
                    new WorkflowNodeProgress(
                        RowsRead: batch.Rows.Count,
                        BatchesProcessed: 1,
                        BytesRead: batch.EstimatedByteCount),
                    cancellationToken);
                yield return output;
            }
        }
    }
}

public sealed class FilterRowsNodeExecutor(
    WorkflowNodeSettingsValidator settingsValidator) : IWorkflowNodeExecutor
{
    public WorkflowNodeExecutorDescriptor Descriptor { get; } =
        WorkflowNodeExecutorDescriptor.Transform(WorkflowNodeKind.FilterRows);

    public ValueTask<WorkflowNodeExecutionResult> ExecuteAsync(
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = (FilterRowsNodeSettings)settingsValidator.Parse(context.Node);
        var input = TabularExecutionSupport.RequireSingleInput(context);
        var predicates = settings.Conditions
            .Select(condition => Compile(input.DataSet.Schema, condition, context.Limits))
            .ToArray();
        return ValueTask.FromResult(WorkflowNodeExecutionResult.WithOutput(
            input.DataSet.Schema,
            FilterAsync(input.DataSet, predicates, context.Progress, cancellationToken)));
    }

    private static Func<TabularRow, bool> Compile(
        TabularSchema schema,
        FilterConditionSettings condition,
        TabularExecutionLimits limits)
    {
        var index = TabularExecutionSupport.FindColumn(schema, condition.Column);
        var column = schema.Columns[index];
        if (condition.Operator is "isNull" or "isNotNull")
        {
            return condition.Operator == "isNull"
                ? row => row.Values[index].Kind == TabularValueKind.Null
                : row => row.Values[index].Kind != TabularValueKind.Null;
        }

        var constant = ParseJsonConstant(condition.Value, column, limits);
        return row => Evaluate(row.Values[index], constant, condition.Operator);
    }

    private static TabularValue ParseJsonConstant(
        JsonElement value,
        TabularColumn column,
        TabularExecutionLimits limits)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return TabularValue.Null;
        }

        var text = value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.GetRawText();
        return TabularExecutionSupport.ParseText(text, column, limits.MaximumStringCharacters);
    }

    private static bool Evaluate(TabularValue actual, TabularValue expected, string operation)
    {
        if (actual.Kind == TabularValueKind.Null || expected.Kind == TabularValueKind.Null)
        {
            return operation switch
            {
                "equals" => actual.Kind == expected.Kind,
                "notEquals" => actual.Kind != expected.Kind,
                _ => false
            };
        }

        if (operation is "contains" or "startsWith" or "endsWith")
        {
            if (actual.Kind != TabularValueKind.String || expected.Kind != TabularValueKind.String)
            {
                throw ExecutionFailure.Configuration(
                    "execution.filter.operator.type",
                    $"Filter operator '{operation}' requires a text column.");
            }

            var left = (string)actual.Value!;
            var right = (string)expected.Value!;
            return operation switch
            {
                "contains" => left.Contains(right, StringComparison.Ordinal),
                "startsWith" => left.StartsWith(right, StringComparison.Ordinal),
                _ => left.EndsWith(right, StringComparison.Ordinal)
            };
        }

        var comparison = Compare(actual, expected);
        return operation switch
        {
            "equals" => comparison == 0,
            "notEquals" => comparison != 0,
            "greaterThan" => comparison > 0,
            "greaterThanOrEqual" => comparison >= 0,
            "lessThan" => comparison < 0,
            "lessThanOrEqual" => comparison <= 0,
            _ => throw ExecutionFailure.Configuration(
                "execution.filter.operator",
                $"Filter operator '{operation}' is not supported.")
        };
    }

    private static int Compare(TabularValue left, TabularValue right)
    {
        if (left.Kind != right.Kind)
        {
            throw ExecutionFailure.Configuration(
                "execution.filter.type",
                "The filter value is incompatible with the input column.");
        }

        return left.Kind switch
        {
            TabularValueKind.Boolean => ((bool)left.Value!).CompareTo((bool)right.Value!),
            TabularValueKind.Int64 => ((long)left.Value!).CompareTo((long)right.Value!),
            TabularValueKind.Decimal => ((decimal)left.Value!).CompareTo((decimal)right.Value!),
            TabularValueKind.Double => ((double)left.Value!).CompareTo((double)right.Value!),
            TabularValueKind.String => string.Compare((string)left.Value!, (string)right.Value!, StringComparison.Ordinal),
            TabularValueKind.DateTimeOffset => ((DateTimeOffset)left.Value!).CompareTo((DateTimeOffset)right.Value!),
            TabularValueKind.Guid => ((Guid)left.Value!).CompareTo((Guid)right.Value!),
            TabularValueKind.Binary => ((ReadOnlyMemory<byte>)left.Value!).Span.SequenceCompareTo(((ReadOnlyMemory<byte>)right.Value!).Span),
            _ => throw new ArgumentOutOfRangeException(nameof(left))
        };
    }

    private static async IAsyncEnumerable<TabularBatch> FilterAsync(
        ITabularDataSet input,
        IReadOnlyList<Func<TabularRow, bool>> predicates,
        IWorkflowNodeProgressReporter progress,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var batch in input.ReadBatchesAsync(cancellationToken).ConfigureAwait(false))
        {
            await using (batch.ConfigureAwait(false))
            {
                var rows = batch.Rows.Where(row => predicates.All(predicate => predicate(row))).ToArray();
                await progress.ReportAsync(
                    new WorkflowNodeProgress(
                        RowsRead: batch.Rows.Count,
                        BatchesProcessed: 1,
                        BytesRead: batch.EstimatedByteCount),
                    cancellationToken);
                if (rows.Length > 0)
                {
                    yield return new TabularBatch(rows);
                }
            }
        }
    }
}

public sealed class JoinNodeExecutor(
    WorkflowNodeSettingsValidator settingsValidator) : IWorkflowNodeExecutor
{
    public WorkflowNodeExecutorDescriptor Descriptor { get; } =
        WorkflowNodeExecutorDescriptor.Transform(WorkflowNodeKind.Join);

    public ValueTask<WorkflowNodeExecutionResult> ExecuteAsync(
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = (JoinNodeSettings)settingsValidator.Parse(context.Node);
        var left = context.Inputs.SingleOrDefault(input => input.SourceNodeId == settings.LeftNodeId);
        var right = context.Inputs.SingleOrDefault(input => input.SourceNodeId == settings.RightNodeId);
        if (left is null || right is null || context.Inputs.Count != 2 || ReferenceEquals(left, right))
        {
            throw ExecutionFailure.Configuration(
                "execution.join.inputs",
                "Join inputs must match the configured left and right node ids exactly.");
        }

        var leftIndexes = settings.LeftKeys
            .Select(key => TabularExecutionSupport.FindColumn(left.DataSet.Schema, key))
            .ToArray();
        var rightIndexes = settings.RightKeys
            .Select(key => TabularExecutionSupport.FindColumn(right.DataSet.Schema, key))
            .ToArray();
        for (var index = 0; index < leftIndexes.Length; index++)
        {
            if (left.DataSet.Schema.Columns[leftIndexes[index]].DataType
                != right.DataSet.Schema.Columns[rightIndexes[index]].DataType)
            {
                throw ExecutionFailure.Configuration(
                    "execution.join.key.type",
                    "Corresponding join keys must have the same data type.");
            }
        }

        var schema = BuildSchema(left.DataSet.Schema, right.DataSet.Schema);
        return ValueTask.FromResult(WorkflowNodeExecutionResult.WithOutput(
            schema,
            JoinAsync(
                left.DataSet,
                right.DataSet,
                leftIndexes,
                rightIndexes,
                settings,
                context,
                cancellationToken)));
    }

    private static TabularSchema BuildSchema(TabularSchema left, TabularSchema right)
    {
        var columns = new List<TabularColumn>(left.Columns);
        var names = new HashSet<string>(left.Columns.Select(column => column.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var column in right.Columns)
        {
            var name = column.Name;
            if (!names.Add(name))
            {
                var baseName = $"right.{column.Name}";
                name = baseName.Length <= TabularColumn.MaximumNameLength
                    ? baseName
                    : baseName[..TabularColumn.MaximumNameLength];
                var suffix = 2;
                while (!names.Add(name))
                {
                    var suffixText = $"_{suffix++}";
                    name = string.Concat(
                        baseName.AsSpan(0, Math.Min(baseName.Length, TabularColumn.MaximumNameLength - suffixText.Length)),
                        suffixText);
                }
            }

            columns.Add(new TabularColumn(name, column.DataType, isNullable: true));
        }

        return new TabularSchema(columns);
    }

    private static async IAsyncEnumerable<TabularBatch> JoinAsync(
        ITabularDataSet left,
        ITabularDataSet right,
        IReadOnlyList<int> leftIndexes,
        IReadOnlyList<int> rightIndexes,
        JoinNodeSettings settings,
        WorkflowNodeExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var lookup = new Dictionary<string, List<TabularRow>>(StringComparer.Ordinal);
        var buffered = 0;
        await foreach (var batch in right.ReadBatchesAsync(cancellationToken).ConfigureAwait(false))
        {
            await using (batch.ConfigureAwait(false))
            {
                foreach (var row in batch.Rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    buffered++;
                    if (buffered > settings.MaximumBufferedRows)
                    {
                        throw new TabularLimitExceededException(
                            "execution.join.buffer.limit",
                            $"The join right side exceeds its {settings.MaximumBufferedRows}-row buffer limit.");
                    }

                    var key = CreateKey(row, rightIndexes);
                    if (key is not null)
                    {
                        if (!lookup.TryGetValue(key, out var matches))
                        {
                            matches = [];
                            lookup.Add(key, matches);
                        }

                        matches.Add(row);
                    }
                }

                await context.Progress.ReportAsync(
                    new WorkflowNodeProgress(
                        RowsRead: batch.Rows.Count,
                        BatchesProcessed: 1,
                        BytesRead: batch.EstimatedByteCount),
                    cancellationToken);
            }
        }

        var outputRows = new List<TabularRow>(context.Limits.MaximumRowsPerBatch);
        await foreach (var batch in left.ReadBatchesAsync(cancellationToken).ConfigureAwait(false))
        {
            await using (batch.ConfigureAwait(false))
            {
                foreach (var row in batch.Rows)
                {
                    var key = CreateKey(row, leftIndexes);
                    if (key is not null && lookup.TryGetValue(key, out var matches))
                    {
                        foreach (var match in matches)
                        {
                            outputRows.Add(new TabularRow(row.Values.Concat(match.Values)));
                            if (outputRows.Count == context.Limits.MaximumRowsPerBatch)
                            {
                                yield return new TabularBatch(outputRows);
                                outputRows = new(context.Limits.MaximumRowsPerBatch);
                            }
                        }
                    }
                    else if (settings.JoinType == "left")
                    {
                        outputRows.Add(new TabularRow(
                            row.Values.Concat(Enumerable.Repeat(
                                TabularValue.Null,
                                right.Schema.Columns.Count))));
                        if (outputRows.Count == context.Limits.MaximumRowsPerBatch)
                        {
                            yield return new TabularBatch(outputRows);
                            outputRows = new(context.Limits.MaximumRowsPerBatch);
                        }
                    }
                }

                await context.Progress.ReportAsync(
                    new WorkflowNodeProgress(
                        RowsRead: batch.Rows.Count,
                        BatchesProcessed: 1,
                        BytesRead: batch.EstimatedByteCount),
                    cancellationToken);
            }
        }

        if (outputRows.Count > 0)
        {
            yield return new TabularBatch(outputRows);
        }
    }

    private static string? CreateKey(TabularRow row, IReadOnlyList<int> indexes)
    {
        var builder = new StringBuilder();
        foreach (var index in indexes)
        {
            var value = row.Values[index];
            if (value.Kind == TabularValueKind.Null)
            {
                return null;
            }

            var text = TabularExecutionSupport.ToInvariantText(value);
            builder.Append((int)value.Kind).Append(':').Append(text.Length).Append(':').Append(text).Append('|');
        }

        return builder.ToString();
    }
}
