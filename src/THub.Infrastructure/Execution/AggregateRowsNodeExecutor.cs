using System.Runtime.CompilerServices;
using THub.Application.Execution;
using THub.Application.Workflows;
using THub.Domain.Workflows;

namespace THub.Infrastructure.Execution;

public sealed class AggregateRowsNodeExecutor(
    WorkflowNodeSettingsValidator settingsValidator) : IWorkflowNodeExecutor
{
    public WorkflowNodeExecutorDescriptor Descriptor { get; } =
        WorkflowNodeExecutorDescriptor.Transform(WorkflowNodeKind.AggregateRows);

    public ValueTask<WorkflowNodeExecutionResult> ExecuteAsync(
        WorkflowNodeExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = (AggregateRowsNodeSettings)settingsValidator.Parse(context.Node);
        var input = TabularExecutionSupport.RequireSingleInput(context);
        var groupIndexes = settings.GroupBy
            .Select(column => TabularExecutionSupport.FindColumn(input.DataSet.Schema, column))
            .ToArray();
        var aggregateInputs = settings.Aggregates
            .Select(aggregate => ResolveAggregateInput(input.DataSet.Schema, aggregate))
            .ToArray();
        TabularSchema schema;
        try
        {
            schema = WorkflowTransformSchemaSemantics.CreateAggregateSchema(
                input.DataSet.Schema,
                settings);
        }
        catch (WorkflowTransformSchemaException exception)
            when (exception.Code == "schema.aggregate.operation.type")
        {
            throw ExecutionFailure.Configuration(
                "execution.aggregate.operation.type",
                exception.Message,
                exception);
        }
        var aggregates = aggregateInputs
            .Select((aggregate, index) => aggregate with
            {
                OutputType = schema.Columns[groupIndexes.Length + index].DataType
            })
            .ToArray();
        return ValueTask.FromResult(WorkflowNodeExecutionResult.WithOutput(
            schema,
            AggregateAsync(
                input.DataSet,
                groupIndexes,
                aggregates,
                settings.MaximumGroups,
                context,
                cancellationToken)));
    }

    private static ResolvedAggregate ResolveAggregateInput(
        TabularSchema schema,
        AggregateColumnSettings settings)
    {
        if (settings.Operation == AggregateOperation.Count)
        {
            return new(settings, null, null, TabularDataType.Int64);
        }

        var index = TabularExecutionSupport.FindColumn(schema, settings.Column!);
        var inputType = schema.Columns[index].DataType;
        return new(settings, index, inputType, default);
    }

    private static async IAsyncEnumerable<TabularBatch> AggregateAsync(
        ITabularDataSet input,
        IReadOnlyList<int> groupIndexes,
        IReadOnlyList<ResolvedAggregate> aggregates,
        int maximumGroups,
        WorkflowNodeExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        AggregateGroup? globalGroup = groupIndexes.Count == 0
            ? new AggregateGroup([], aggregates)
            : null;
        var groups = globalGroup is null
            ? new TransformStructuralKeyMap<AggregateGroup>()
            : null;
        var orderedGroups = new List<AggregateGroup>();

        await foreach (var batch in input.ReadBatchesAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            await using (batch.ConfigureAwait(false))
            {
                foreach (var row in batch.Rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (globalGroup is not null)
                    {
                        globalGroup.Add(row);
                        continue;
                    }

                    var addResult = groups!.GetOrAdd(
                        row,
                        groupIndexes,
                        maximumGroups,
                        aggregates,
                        static (ownedKey, resolvedAggregates) => new AggregateGroup(
                            ownedKey.Values,
                            resolvedAggregates),
                        cancellationToken,
                        out var group);
                    if (addResult == TransformKeyAddResult.LimitExceeded)
                    {
                        throw new TabularLimitExceededException(
                            "execution.aggregate.group.limit",
                            $"Aggregate input exceeds the configured {maximumGroups}-group limit.");
                    }
                    if (addResult == TransformKeyAddResult.Added)
                    {
                        orderedGroups.Add(group);
                    }
                    group.Add(row);
                }

                await context.Progress.ReportAsync(
                    new WorkflowNodeProgress(
                        RowsRead: batch.Rows.Count,
                        BatchesProcessed: 1,
                        BytesRead: batch.EstimatedByteCount),
                    cancellationToken);
            }
        }

        var output = globalGroup is not null
            ? new[] { globalGroup.ToRow() }
            : orderedGroups.Select(group => group.ToRow()).ToArray();
        await foreach (var batch in TransformValueSupport.BatchRowsAsync(
                           output,
                           context.Limits.MaximumRowsPerBatch,
                           cancellationToken))
        {
            yield return batch;
        }
    }

    private sealed class AggregateGroup
    {
        private readonly IReadOnlyList<TabularValue> groupValues;
        private readonly AggregateState[] states;

        public AggregateGroup(
            IReadOnlyList<TabularValue> groupValues,
            IReadOnlyList<ResolvedAggregate> aggregates)
        {
            this.groupValues = groupValues;
            states = aggregates.Select(aggregate => new AggregateState(aggregate)).ToArray();
        }

        public void Add(TabularRow row)
        {
            foreach (var state in states)
            {
                state.Add(row);
            }
        }

        public TabularRow ToRow() =>
            new(groupValues.Concat(states.Select(state => state.GetValue())));
    }

    private sealed class AggregateState(ResolvedAggregate aggregate)
    {
        private long count;
        private long int64Sum;
        private decimal decimalSum;
        private decimal decimalAverage;
        private double doubleSum;
        private TabularValue selected;
        private bool hasValue;

        public void Add(TabularRow row)
        {
            try
            {
                if (aggregate.Settings.Operation == AggregateOperation.Count)
                {
                    count = checked(count + 1);
                    return;
                }

                var value = row.Values[aggregate.InputIndex!.Value];
                if (value.Kind == TabularValueKind.Null)
                {
                    return;
                }

                switch (aggregate.Settings.Operation)
                {
                    case AggregateOperation.CountNonNull:
                        count = checked(count + 1);
                        break;
                    case AggregateOperation.Sum:
                        AddSum(value);
                        break;
                    case AggregateOperation.Average:
                        AddAverage(value);
                        break;
                    case AggregateOperation.Minimum:
                        if (!hasValue || TransformValueSupport.Compare(value, selected) < 0)
                        {
                            selected = value;
                            hasValue = true;
                        }
                        break;
                    case AggregateOperation.Maximum:
                        if (!hasValue || TransformValueSupport.Compare(value, selected) > 0)
                        {
                            selected = value;
                            hasValue = true;
                        }
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(aggregate));
                }
            }
            catch (OverflowException exception)
            {
                throw NumericOverflow(exception);
            }
        }

        public TabularValue GetValue() => aggregate.Settings.Operation switch
        {
            AggregateOperation.Count or AggregateOperation.CountNonNull =>
                TabularValue.From(count),
            AggregateOperation.Sum => hasValue ? SumValue() : TabularValue.Null,
            AggregateOperation.Average => hasValue ? AverageValue() : TabularValue.Null,
            AggregateOperation.Minimum or AggregateOperation.Maximum =>
                hasValue ? selected : TabularValue.Null,
            _ => throw new ArgumentOutOfRangeException(nameof(aggregate))
        };

        private void AddSum(TabularValue value)
        {
            switch (aggregate.InputType)
            {
                case TabularDataType.Int64:
                    int64Sum = checked(int64Sum + (long)value.Value!);
                    break;
                case TabularDataType.Decimal:
                    decimalSum = checked(decimalSum + (decimal)value.Value!);
                    break;
                case TabularDataType.Double:
                    doubleSum += (double)value.Value!;
                    EnsureFinite(doubleSum);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(aggregate));
            }

            hasValue = true;
        }

        private void AddAverage(TabularValue value)
        {
            count = checked(count + 1);
            switch (aggregate.InputType)
            {
                case TabularDataType.Int64:
                    AddDecimalAverage((long)value.Value!);
                    break;
                case TabularDataType.Decimal:
                    AddDecimalAverage((decimal)value.Value!);
                    break;
                case TabularDataType.Double:
                    doubleSum += (double)value.Value!;
                    EnsureFinite(doubleSum);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(aggregate));
            }

            hasValue = true;
        }

        private void AddDecimalAverage(decimal value)
        {
            decimalAverage = checked(
                decimalAverage
                - (decimalAverage / count)
                + (value / count));
        }

        private static void EnsureFinite(double value)
        {
            if (!double.IsFinite(value))
            {
                throw NumericOverflow();
            }
        }

        private TabularValue SumValue() => aggregate.InputType switch
        {
            TabularDataType.Int64 => TabularValue.From(int64Sum),
            TabularDataType.Decimal => TabularValue.From(decimalSum),
            TabularDataType.Double => TabularValue.From(doubleSum),
            _ => throw new ArgumentOutOfRangeException(nameof(aggregate))
        };

        private TabularValue AverageValue() => aggregate.InputType switch
        {
            TabularDataType.Int64 or TabularDataType.Decimal =>
                TabularValue.From(decimalAverage),
            TabularDataType.Double => FiniteDoubleAverage(),
            _ => throw new ArgumentOutOfRangeException(nameof(aggregate))
        };

        private TabularValue FiniteDoubleAverage()
        {
            var value = doubleSum / count;
            EnsureFinite(value);
            return TabularValue.From(value);
        }
    }

    private static WorkflowNodeExecutionException NumericOverflow(Exception? exception = null) =>
        ExecutionFailure.Data(
            "execution.aggregate.numeric.overflow",
            "Aggregate numeric arithmetic exceeded its supported finite range.",
            exception);

    private sealed record ResolvedAggregate(
        AggregateColumnSettings Settings,
        int? InputIndex,
        TabularDataType? InputType,
        TabularDataType OutputType);
}
