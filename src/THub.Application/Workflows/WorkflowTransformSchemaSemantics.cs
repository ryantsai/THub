using THub.Application.Execution;

namespace THub.Application.Workflows;

public sealed class WorkflowTransformSchemaException(
    string code,
    string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record WorkflowUnionSchemaPlan(
    TabularSchema Schema,
    IReadOnlyList<IReadOnlyList<int>> Alignments);

/// <summary>
/// Pure schema rules shared by designer propagation and runtime transform executors.
/// </summary>
public static class WorkflowTransformSchemaSemantics
{
    public static TabularSchema CreateJoinSchema(
        TabularSchema left,
        TabularSchema right,
        string joinType)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentException.ThrowIfNullOrWhiteSpace(joinType);

        var leftNullable = joinType is "right" or "full";
        var rightNullable = joinType is "left" or "full";
        var columns = new List<TabularColumn>(left.Columns.Count + right.Columns.Count);
        foreach (var column in left.Columns)
        {
            columns.Add(leftNullable
                ? new TabularColumn(column.Name, column.DataType, isNullable: true)
                : column);
        }

        var names = left.Columns
            .Select(column => column.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var column in right.Columns)
        {
            columns.Add(new(
                CreateRightColumnName(column.Name, names),
                column.DataType,
                rightNullable || column.IsNullable));
        }
        return new(columns);
    }

    public static WorkflowUnionSchemaPlan CreateUnionPlan(
        IReadOnlyList<TabularSchema> inputs,
        UnionMatchMode matchBy)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inputs),
                "Union schema calculation requires at least one input.");
        }

        var first = inputs[0];
        ArgumentNullException.ThrowIfNull(first);
        var alignments = new List<IReadOnlyList<int>>(inputs.Count)
        {
            Enumerable.Range(0, first.Columns.Count).ToArray()
        };
        var nullable = first.Columns.Select(column => column.IsNullable).ToArray();
        foreach (var candidate in inputs.Skip(1))
        {
            ArgumentNullException.ThrowIfNull(candidate);
            var alignment = matchBy == UnionMatchMode.Name
                ? AlignUnionByName(first, candidate)
                : AlignUnionByPosition(first, candidate);
            for (var index = 0; index < alignment.Count; index++)
            {
                nullable[index] |= candidate.Columns[alignment[index]].IsNullable;
            }
            alignments.Add(alignment);
        }

        var schema = new TabularSchema(first.Columns.Select((column, index) =>
            new TabularColumn(column.Name, column.DataType, nullable[index])));
        return new(schema, alignments.AsReadOnly());
    }

    public static TabularSchema CreateAggregateSchema(
        TabularSchema input,
        AggregateRowsNodeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(settings);

        var columns = new List<TabularColumn>(
            settings.GroupBy.Count + settings.Aggregates.Count);
        foreach (var groupName in settings.GroupBy)
        {
            columns.Add(FindColumn(input, groupName));
        }
        foreach (var aggregate in settings.Aggregates)
        {
            columns.Add(CreateAggregateColumn(input, aggregate));
        }
        return new(columns);
    }

    private static string CreateRightColumnName(
        string sourceName,
        HashSet<string> names)
    {
        if (names.Add(sourceName))
        {
            return sourceName;
        }

        var baseName = $"right.{sourceName}";
        var candidate = baseName.Length <= TabularColumn.MaximumNameLength
            ? baseName
            : baseName[..TabularColumn.MaximumNameLength];
        var suffix = 2;
        while (!names.Add(candidate))
        {
            var suffixText = $"_{suffix++}";
            candidate = string.Concat(
                baseName.AsSpan(
                    0,
                    Math.Min(
                        baseName.Length,
                        TabularColumn.MaximumNameLength - suffixText.Length)),
                suffixText);
        }
        return candidate;
    }

    private static IReadOnlyList<int> AlignUnionByName(
        TabularSchema first,
        TabularSchema candidate)
    {
        if (candidate.Columns.Count != first.Columns.Count)
        {
            throw UnionIncompatible(
                $"Expected {first.Columns.Count} columns but found {candidate.Columns.Count}.");
        }

        var byName = candidate.Columns
            .Select((column, index) => (column, index))
            .ToDictionary(
                item => item.column.Name,
                item => item.index,
                StringComparer.OrdinalIgnoreCase);
        var alignment = new int[first.Columns.Count];
        for (var index = 0; index < first.Columns.Count; index++)
        {
            var expected = first.Columns[index];
            if (!byName.TryGetValue(expected.Name, out var candidateIndex))
            {
                throw UnionIncompatible($"Column '{expected.Name}' is missing.");
            }
            var actual = candidate.Columns[candidateIndex];
            if (actual.DataType != expected.DataType)
            {
                throw UnionIncompatible(
                    $"Column '{expected.Name}' has incompatible types {expected.DataType} and {actual.DataType}.");
            }
            alignment[index] = candidateIndex;
        }
        return alignment;
    }

    private static IReadOnlyList<int> AlignUnionByPosition(
        TabularSchema first,
        TabularSchema candidate)
    {
        if (candidate.Columns.Count != first.Columns.Count)
        {
            throw UnionIncompatible(
                $"Expected {first.Columns.Count} columns but found {candidate.Columns.Count}.");
        }

        var alignment = new int[first.Columns.Count];
        for (var index = 0; index < first.Columns.Count; index++)
        {
            if (candidate.Columns[index].DataType != first.Columns[index].DataType)
            {
                throw UnionIncompatible(
                    $"Column position {index + 1} has incompatible types {first.Columns[index].DataType} and {candidate.Columns[index].DataType}.");
            }
            alignment[index] = index;
        }
        return alignment;
    }

    private static TabularColumn CreateAggregateColumn(
        TabularSchema input,
        AggregateColumnSettings aggregate)
    {
        if (aggregate.Operation == AggregateOperation.Count)
        {
            return new(aggregate.Name, TabularDataType.Int64, isNullable: false);
        }

        var source = FindColumn(input, aggregate.Column!);
        var type = aggregate.Operation switch
        {
            AggregateOperation.CountNonNull => TabularDataType.Int64,
            AggregateOperation.Sum when IsNumeric(source.DataType) => source.DataType,
            AggregateOperation.Average
                when source.DataType is TabularDataType.Int64 or TabularDataType.Decimal =>
                TabularDataType.Decimal,
            AggregateOperation.Average when source.DataType == TabularDataType.Double =>
                TabularDataType.Double,
            AggregateOperation.Minimum or AggregateOperation.Maximum => source.DataType,
            AggregateOperation.Sum or AggregateOperation.Average =>
                throw new WorkflowTransformSchemaException(
                    "schema.aggregate.operation.type",
                    $"Aggregate operation '{aggregate.Operation}' requires a numeric input column."),
            _ => throw new ArgumentOutOfRangeException(nameof(aggregate))
        };
        return new(
            aggregate.Name,
            type,
            aggregate.Operation is not AggregateOperation.CountNonNull);
    }

    private static TabularColumn FindColumn(TabularSchema schema, string name)
    {
        var column = schema.Columns.FirstOrDefault(
            candidate => string.Equals(
                candidate.Name,
                name,
                StringComparison.OrdinalIgnoreCase));
        return column ?? throw new WorkflowTransformSchemaException(
            "schema.column.unresolved",
            $"Column '{name}' does not exist in the input schema.");
    }

    private static bool IsNumeric(TabularDataType type) =>
        type is TabularDataType.Int64
            or TabularDataType.Decimal
            or TabularDataType.Double;

    private static WorkflowTransformSchemaException UnionIncompatible(string detail) =>
        new(
            "schema.union.incompatible",
            $"Union input schemas are incompatible. {detail}");
}
