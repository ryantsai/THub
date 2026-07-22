using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;
using THub.Application.Publications;
using THub.Domain.Publications;

namespace THub.Infrastructure.Publications;

internal enum SqlPublicationPlanStatus
{
    Success,
    InvalidQuery,
    InvalidCursor,
}

internal sealed record SqlPublicationReadPlan(
    string CommandText,
    IReadOnlyList<SqlParameter> Parameters,
    IReadOnlyList<PublicationColumn> OutputColumns,
    IReadOnlyList<PublicationFilter> Filters,
    IReadOnlyList<SqlPublicationSortTerm> Sorts,
    int Take);

internal sealed record SqlPublicationPlanResult(
    SqlPublicationPlanStatus Status,
    SqlPublicationReadPlan? Plan);

internal static class SqlPublicationQueryPlanner
{
    private const string OversizeAlias = "__thub_oversize";
    private const int AbsoluteMaximumCellBytes = 2 * 1_024 * 1_024;

    public static SqlPublicationPlanResult BuildRows(
        PublicationVersion version,
        PublicationSourceReadQuery query,
        int connectorMaximumRows)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(query);
        var maximumRows = Math.Min(
            connectorMaximumRows,
            Math.Max(version.Settings.MaximumPageSize, version.Settings.EditorWindowSize));
        if (query.Take < 1 ||
            query.Take > maximumRows ||
            query.Filters.Count > 16 ||
            query.Sorts.Count > 24)
        {
            return InvalidQuery();
        }

        var columns = version.Columns.ToDictionary(
            column => column.PublicAlias,
            StringComparer.OrdinalIgnoreCase);
        var outputColumns = version.Columns
            .Where(column => column.IsReadable)
            .OrderBy(column => column.Ordinal)
            .ToArray();
        if (outputColumns.Length == 0)
        {
            return InvalidQuery();
        }

        var sorts = new List<SqlPublicationSortTerm>();
        var sortAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requestedSort in query.Sorts)
        {
            if (requestedSort is null ||
                !columns.TryGetValue(requestedSort.ColumnAlias, out var column) ||
                !column.IsReadable ||
                !column.IsSortable ||
                !sortAliases.Add(column.PublicAlias))
            {
                return InvalidQuery();
            }

            sorts.Add(new SqlPublicationSortTerm(
                column,
                requestedSort.Descending,
                QuoteIdentifier(column.SourceName)));
        }

        foreach (var keyColumn in version.Columns
                     .Where(column => column.IsKey)
                     .OrderBy(column => column.KeyOrdinal))
        {
            if (sortAliases.Add(keyColumn.PublicAlias))
            {
                sorts.Add(new SqlPublicationSortTerm(
                    keyColumn,
                    false,
                    QuoteIdentifier(keyColumn.SourceName)));
            }
        }

        if (sorts.Count == 0 || sorts.All(sort => !sort.Column.IsKey))
        {
            return InvalidQuery();
        }

        var parameters = new List<SqlParameter>
        {
            new("@__take", SqlDbType.Int) { Value = checked(query.Take + 1) },
        };
        var whereParts = new List<string>();
        for (var index = 0; index < query.Filters.Count; index++)
        {
            var filter = query.Filters[index];
            if (!TryBuildFilter(filter, index, columns, parameters, out var clause))
            {
                return InvalidQuery();
            }

            whereParts.Add(clause);
        }

        if (query.Cursor is not null)
        {
            if (!SqlPublicationCursorCodec.TryDecode(
                    query.Cursor,
                    version,
                    query.Filters,
                    sorts,
                    out var cursorValues))
            {
                return new SqlPublicationPlanResult(SqlPublicationPlanStatus.InvalidCursor, null);
            }

            whereParts.Add(BuildCursorPredicate(sorts, cursorValues, parameters));
        }

        var maximumCellBytes = Math.Min(
            AbsoluteMaximumCellBytes,
            version.Settings.MaximumResponseBytes);
        var guardedColumns = outputColumns
            .Where(column => column.DataType is PublicationDataType.String or PublicationDataType.Binary)
            .ToArray();
        if (guardedColumns.Length > 0)
        {
            parameters.Add(new SqlParameter("@__maxCellBytes", SqlDbType.Int)
            {
                Value = maximumCellBytes,
            });
        }

        var select = new StringBuilder();
        select.Append("SELECT TOP (@__take) ");
        AppendOversizeExpression(select, guardedColumns);
        foreach (var column in outputColumns)
        {
            select.Append(", ");
            AppendSelectedColumn(select, column, guardedColumns.Length > 0);
        }

        select.Append(" FROM ")
            .Append(QuoteIdentifier(version.SourceSchema))
            .Append('.')
            .Append(QuoteIdentifier(version.SourceObject));
        if (whereParts.Count > 0)
        {
            select.Append(" WHERE ").AppendJoin(" AND ", whereParts.Select(part => $"({part})"));
        }

        select.Append(" ORDER BY ");
        select.AppendJoin(
            ", ",
            sorts.Select(sort => $"{sort.SqlExpression} {(sort.Descending ? "DESC" : "ASC")}"));
        select.Append(';');

        return new SqlPublicationPlanResult(
            SqlPublicationPlanStatus.Success,
            new SqlPublicationReadPlan(
                select.ToString(),
                parameters,
                outputColumns,
                query.Filters,
                sorts,
                query.Take));
    }

    public static string QuoteIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        if (identifier.Length > 128 || identifier.Any(char.IsControl))
        {
            throw new ArgumentException("SQL Server identifier is invalid.", nameof(identifier));
        }

        return $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    private static bool TryBuildFilter(
        PublicationFilter filter,
        int index,
        IReadOnlyDictionary<string, PublicationColumn> columns,
        List<SqlParameter> parameters,
        out string clause)
    {
        clause = string.Empty;
        if (filter is null ||
            !Enum.IsDefined(filter.Operator) ||
            filter.Value?.Length > 4_096 ||
            !columns.TryGetValue(filter.ColumnAlias, out var column) ||
            !column.IsReadable ||
            !column.IsFilterable)
        {
            return false;
        }

        var expression = QuoteIdentifier(column.SourceName);
        if (filter.Operator == PublicationFilterOperator.IsNull)
        {
            clause = $"{expression} IS NULL";
            return filter.Value is null;
        }

        if (filter.Operator == PublicationFilterOperator.IsNotNull)
        {
            clause = $"{expression} IS NOT NULL";
            return filter.Value is null;
        }

        if (filter.Value is null)
        {
            return false;
        }

        var parameterName = $"@__filter{index}";
        if (filter.Operator is PublicationFilterOperator.StartsWith or PublicationFilterOperator.Contains)
        {
            if (column.DataType != PublicationDataType.String)
            {
                return false;
            }

            var escaped = EscapeLikeValue(filter.Value);
            var pattern = filter.Operator == PublicationFilterOperator.StartsWith
                ? $"{escaped}%"
                : $"%{escaped}%";
            var parameter = SqlPublicationValueConverter.CreateParameter(parameterName, pattern, column);
            if (parameter.Size > 0 && parameter.Size < pattern.Length)
            {
                parameter.Size = pattern.Length;
            }

            parameters.Add(parameter);
            clause = $"{expression} LIKE {parameterName} ESCAPE N'~'";
            return true;
        }

        if (!SqlPublicationValueConverter.TryParse(filter.Value, column, out var parsed))
        {
            return false;
        }

        parameters.Add(SqlPublicationValueConverter.CreateParameter(parameterName, parsed, column));
        var sqlOperator = filter.Operator switch
        {
            PublicationFilterOperator.Equal => "=",
            PublicationFilterOperator.NotEqual => "<>",
            PublicationFilterOperator.GreaterThan => ">",
            PublicationFilterOperator.GreaterThanOrEqual => ">=",
            PublicationFilterOperator.LessThan => "<",
            PublicationFilterOperator.LessThanOrEqual => "<=",
            _ => null,
        };
        if (sqlOperator is null)
        {
            return false;
        }

        clause = $"{expression} {sqlOperator} {parameterName}";
        return true;
    }

    internal static string BuildCursorPredicate(
        IReadOnlyList<SqlPublicationSortTerm> sorts,
        IReadOnlyList<object?> values,
        List<SqlParameter> parameters)
    {
        var branches = new List<string>();
        var equalParts = new List<string>();
        for (var index = 0; index < sorts.Count; index++)
        {
            var sort = sorts[index];
            var value = values[index];
            var expression = sort.SqlExpression;
            string? after;
            if (value is null)
            {
                after = sort.Descending ? null : $"{expression} IS NOT NULL";
                equalParts.Add($"{expression} IS NULL");
            }
            else
            {
                var parameterName = $"@__cursor{index}";
                parameters.Add(SqlPublicationValueConverter.CreateParameter(
                    parameterName,
                    value,
                    sort.Column));
                after = sort.Descending
                    ? $"({expression} < {parameterName} OR {expression} IS NULL)"
                    : $"{expression} > {parameterName}";
                if (after is not null)
                {
                    var prefix = equalParts.Count == 0
                        ? string.Empty
                        : $"{string.Join(" AND ", equalParts)} AND ";
                    branches.Add($"({prefix}{after})");
                }

                equalParts.Add($"{expression} = {parameterName}");
                continue;
            }

            if (after is not null)
            {
                var prefix = equalParts.Count == 1
                    ? string.Empty
                    : $"{string.Join(" AND ", equalParts.Take(equalParts.Count - 1))} AND ";
                branches.Add($"({prefix}{after})");
            }
        }

        return branches.Count == 0 ? "1 = 0" : $"({string.Join(" OR ", branches)})";
    }

    private static void AppendOversizeExpression(
        StringBuilder builder,
        IReadOnlyList<PublicationColumn> guardedColumns)
    {
        if (guardedColumns.Count == 0)
        {
            builder.Append("CAST(0 AS bit) AS ").Append(QuoteIdentifier(OversizeAlias));
            return;
        }

        builder.Append("CAST(CASE WHEN ");
        builder.AppendJoin(
            " OR ",
            guardedColumns.Select(column =>
                $"DATALENGTH({QuoteIdentifier(column.SourceName)}) > @__maxCellBytes"));
        builder.Append(" THEN 1 ELSE 0 END AS bit) AS ").Append(QuoteIdentifier(OversizeAlias));
    }

    private static void AppendSelectedColumn(
        StringBuilder builder,
        PublicationColumn column,
        bool hasCellGuard)
    {
        var source = QuoteIdentifier(column.SourceName);
        if (hasCellGuard && column.DataType is PublicationDataType.String or PublicationDataType.Binary)
        {
            builder.Append("CASE WHEN DATALENGTH(")
                .Append(source)
                .Append(") <= @__maxCellBytes THEN ")
                .Append(source)
                .Append(" ELSE NULL END");
        }
        else
        {
            builder.Append(source);
        }

        builder.Append(" AS ").Append(QuoteIdentifier(column.PublicAlias));
    }

    internal static string EscapeLikeValue(string value) => value
        .Replace("~", "~~", StringComparison.Ordinal)
        .Replace("%", "~%", StringComparison.Ordinal)
        .Replace("_", "~_", StringComparison.Ordinal)
        .Replace("[", "~[", StringComparison.Ordinal);

    private static SqlPublicationPlanResult InvalidQuery() =>
        new(SqlPublicationPlanStatus.InvalidQuery, null);
}
