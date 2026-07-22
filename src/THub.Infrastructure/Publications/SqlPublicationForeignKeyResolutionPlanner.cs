using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using THub.Application.Publications;
using THub.Domain.Publications;

namespace THub.Infrastructure.Publications;

internal sealed record SqlPublicationForeignKeyResolutionBatch(
    string CommandText,
    IReadOnlyList<SqlParameter> Parameters);

internal sealed record SqlPublicationForeignKeyResolutionGroupPlan(
    PublicationForeignKey ForeignKey,
    IReadOnlyList<(PublicationColumn LocalColumn, string ReferencedColumn)> KeyColumns,
    IReadOnlyList<SqlPublicationForeignKeyResolutionBatch> Batches);

internal sealed record SqlPublicationForeignKeyResolutionPlanResult(
    SqlPublicationPlanStatus Status,
    IReadOnlyList<SqlPublicationForeignKeyResolutionGroupPlan> Groups);

internal static class SqlPublicationForeignKeyResolutionPlanner
{
    private const int MaximumTuples = PublicationDataService.MaximumForeignKeyTuples;
    private const int MaximumBatchParameters = 1_900;
    private const int MaximumBatchRows = 100;

    public static SqlPublicationForeignKeyResolutionPlanResult Build(
        PublicationVersion version,
        IReadOnlyList<PublicationForeignKeyTuple> tuples)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(tuples);
        if (tuples.Count is < 1 or > MaximumTuples ||
            tuples.Any(tuple => tuple is null || tuple.RequestId < 0) ||
            tuples.Select(tuple => tuple.RequestId).Distinct().Count() != tuples.Count)
        {
            return Invalid();
        }

        var plans = new List<SqlPublicationForeignKeyResolutionGroupPlan>();
        foreach (var tupleGroup in tuples.GroupBy(
                     tuple => tuple.ConstraintName,
                     StringComparer.OrdinalIgnoreCase))
        {
            var keyColumns = version.Columns
                .Where(column => string.Equals(
                    column.ForeignKey?.ConstraintName,
                    tupleGroup.Key,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(column => column.ForeignKey!.Ordinal)
                .Select(column => (LocalColumn: column, ReferencedColumn: column.ForeignKey!.ReferencedColumn))
                .ToArray();
            if (keyColumns.Length == 0 ||
                keyColumns.Length != keyColumns[0].LocalColumn.ForeignKey!.ColumnCount)
            {
                return Invalid();
            }

            var foreignKey = keyColumns[0].LocalColumn.ForeignKey!;
            if (foreignKey.SearchColumns.Count == 0 ||
                keyColumns.Any(key => !key.LocalColumn.IsReadable) ||
                keyColumns.Any(key =>
                    key.LocalColumn.ForeignKey is null ||
                    !string.Equals(key.LocalColumn.ForeignKey.ReferencedSchema, foreignKey.ReferencedSchema, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(key.LocalColumn.ForeignKey.ReferencedObject, foreignKey.ReferencedObject, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(key.LocalColumn.ForeignKey.DisplayColumn, foreignKey.DisplayColumn, StringComparison.OrdinalIgnoreCase) ||
                    !key.LocalColumn.ForeignKey.SearchColumns.SequenceEqual(foreignKey.SearchColumns, StringComparer.OrdinalIgnoreCase) ||
                    key.LocalColumn.ForeignKey.LookupMode != foreignKey.LookupMode) ||
                (foreignKey.IsComposite &&
                 (keyColumns.Select(key => key.LocalColumn.IsWritable).Distinct().Count() != 1 ||
                  keyColumns.Select(key => key.LocalColumn.IsNullable).Distinct().Count() != 1)))
            {
                return Invalid();
            }

            var aliases = keyColumns
                .Select(key => key.LocalColumn.PublicAlias)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var normalized = new List<(int RequestId, IReadOnlyList<object?> Values)>();
            foreach (var tuple in tupleGroup)
            {
                if (tuple.KeyValues is null || tuple.KeyValues.Count != aliases.Count ||
                    tuple.KeyValues.Keys.Any(key => !aliases.Contains(key)))
                {
                    return Invalid();
                }

                var values = new object?[keyColumns.Length];
                for (var index = 0; index < keyColumns.Length; index++)
                {
                    if (!tuple.KeyValues.TryGetValue(keyColumns[index].LocalColumn.PublicAlias, out var value) ||
                        value is null or DBNull ||
                        !TryNormalize(value, keyColumns[index].LocalColumn, out values[index]))
                    {
                        return Invalid();
                    }
                }

                normalized.Add((tuple.RequestId, values));
            }

            var batchSize = Math.Min(MaximumBatchRows, MaximumBatchParameters / keyColumns.Length);
            var batches = new List<SqlPublicationForeignKeyResolutionBatch>();
            for (var offset = 0; offset < normalized.Count; offset += batchSize)
            {
                batches.Add(BuildBatch(
                    foreignKey,
                    keyColumns,
                    normalized.Skip(offset).Take(batchSize).ToArray()));
            }

            plans.Add(new SqlPublicationForeignKeyResolutionGroupPlan(
                foreignKey,
                keyColumns,
                batches));
        }

        return new SqlPublicationForeignKeyResolutionPlanResult(
            SqlPublicationPlanStatus.Success,
            plans);
    }

    private static SqlPublicationForeignKeyResolutionBatch BuildBatch(
        PublicationForeignKey foreignKey,
        IReadOnlyList<(PublicationColumn LocalColumn, string ReferencedColumn)> keyColumns,
        IReadOnlyList<(int RequestId, IReadOnlyList<object?> Values)> rows)
    {
        var parameters = new List<SqlParameter>(rows.Count * keyColumns.Count);
        var sql = new StringBuilder("SELECT candidate.[__request_id], CONVERT(nvarchar(4000), [target].")
            .Append(SqlPublicationQueryPlanner.QuoteIdentifier(foreignKey.DisplayColumn))
            .Append(") AS [__display] FROM (VALUES ");
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            if (rowIndex > 0)
            {
                sql.Append(", ");
            }

            sql.Append('(').Append(rows[rowIndex].RequestId.ToString(CultureInfo.InvariantCulture));
            for (var columnIndex = 0; columnIndex < keyColumns.Count; columnIndex++)
            {
                var parameterName = $"@__fk{rowIndex}_{columnIndex}";
                sql.Append(", ").Append(parameterName);
                parameters.Add(SqlPublicationValueConverter.CreateParameter(
                    parameterName,
                    rows[rowIndex].Values[columnIndex],
                    keyColumns[columnIndex].LocalColumn));
            }

            sql.Append(')');
        }

        sql.Append(") AS candidate([__request_id]");
        for (var index = 0; index < keyColumns.Count; index++)
        {
            sql.Append(", [__key").Append(index.ToString(CultureInfo.InvariantCulture)).Append(']');
        }

        sql.Append(") INNER JOIN ")
            .Append(SqlPublicationQueryPlanner.QuoteIdentifier(foreignKey.ReferencedSchema))
            .Append('.')
            .Append(SqlPublicationQueryPlanner.QuoteIdentifier(foreignKey.ReferencedObject))
            .Append(" AS [target] ON ");
        sql.AppendJoin(
            " AND ",
            keyColumns.Select((key, index) =>
                $"[target].{SqlPublicationQueryPlanner.QuoteIdentifier(key.ReferencedColumn)} = candidate.[__key{index}]"));
        sql.Append(';');

        return new SqlPublicationForeignKeyResolutionBatch(sql.ToString(), parameters);
    }

    private static bool TryNormalize(
        object value,
        PublicationColumn column,
        out object? normalized)
    {
        if (column.DataType == PublicationDataType.String && value is string text)
        {
            normalized = text;
            return column.MaximumLength is null || text.Length <= column.MaximumLength;
        }

        if (column.DataType == PublicationDataType.Binary && value is byte[] binary)
        {
            normalized = binary;
            return column.MaximumLength is null || binary.Length <= column.MaximumLength;
        }

        try
        {
            var formatted = value is string source
                ? source
                : SqlPublicationValueConverter.FormatCursorValue(value, column.DataType);
            return SqlPublicationValueConverter.TryParse(formatted, column, out normalized);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            normalized = null;
            return false;
        }
    }

    private static SqlPublicationForeignKeyResolutionPlanResult Invalid() =>
        new(SqlPublicationPlanStatus.InvalidQuery, []);
}
