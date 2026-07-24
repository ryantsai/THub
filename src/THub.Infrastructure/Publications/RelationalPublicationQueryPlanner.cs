using System.Globalization;
using System.Text;
using THub.Application.Publications;
using THub.Domain.Connections;
using THub.Domain.Publications;
using THub.Infrastructure.Execution;

namespace THub.Infrastructure.Publications;

internal sealed record RelationalPublicationParameter(
    string Name,
    object? Value,
    PublicationColumn Column);

internal sealed record RelationalPublicationReadPlan(
    string CommandText,
    IReadOnlyList<RelationalPublicationParameter> Parameters,
    IReadOnlyList<PublicationColumn> OutputColumns,
    IReadOnlyList<PublicationFilter> Filters,
    IReadOnlyList<SqlPublicationSortTerm> Sorts,
    int Take);

internal sealed record RelationalPublicationPlanResult(
    SqlPublicationPlanStatus Status,
    RelationalPublicationReadPlan? Plan);

internal static class RelationalPublicationQueryPlanner
{
    private const int AbsoluteMaximumCellBytes = 2 * 1_024 * 1_024;

    public static RelationalPublicationPlanResult BuildRows(
        ConnectionKind kind,
        PublicationVersion version,
        PublicationSourceReadQuery query,
        int connectorMaximumRows)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(query);
        var maximumRows = Math.Min(
            connectorMaximumRows,
            Math.Max(version.Settings.MaximumPageSize, version.Settings.EditorWindowSize));
        if (kind is not (ConnectionKind.MySql or ConnectionKind.PostgreSql or ConnectionKind.Oracle) ||
            query.Take < 1 ||
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
        foreach (var requested in query.Sorts)
        {
            if (!columns.TryGetValue(requested.ColumnAlias, out var column) ||
                !column.IsReadable ||
                !column.IsSortable ||
                !sortAliases.Add(column.PublicAlias))
            {
                return InvalidQuery();
            }

            sorts.Add(new SqlPublicationSortTerm(
                column,
                requested.Descending,
                Quote(kind, column.SourceName)));
        }

        foreach (var key in version.Columns
                     .Where(column => column.IsKey)
                     .OrderBy(column => column.KeyOrdinal))
        {
            if (sortAliases.Add(key.PublicAlias))
            {
                sorts.Add(new SqlPublicationSortTerm(key, false, Quote(kind, key.SourceName)));
            }
        }

        if (sorts.Count == 0 || sorts.All(sort => !sort.Column.IsKey))
        {
            return InvalidQuery();
        }

        var parameters = new List<RelationalPublicationParameter>();
        var where = new List<string>();
        for (var index = 0; index < query.Filters.Count; index++)
        {
            if (!TryBuildFilter(
                    kind,
                    query.Filters[index],
                    index,
                    columns,
                    parameters,
                    out var clause))
            {
                return InvalidQuery();
            }

            where.Add(clause);
        }

        if (query.Cursor is not null)
        {
            if (!SqlPublicationCursorCodec.TryDecode(
                    query.Cursor,
                    version,
                    query.Filters,
                    sorts,
                    out var values))
            {
                return new(SqlPublicationPlanStatus.InvalidCursor, null);
            }

            where.Add(BuildCursorPredicate(kind, sorts, values, parameters));
        }

        var maximumCellBytes = Math.Min(
            AbsoluteMaximumCellBytes,
            version.Settings.MaximumResponseBytes);
        var guarded = outputColumns
            .Where(column => column.DataType is PublicationDataType.String or PublicationDataType.Binary)
            .ToArray();
        var sql = new StringBuilder("SELECT ");
        if (guarded.Length == 0)
        {
            sql.Append("0");
        }
        else
        {
            sql.Append("CASE WHEN ")
                .AppendJoin(
                    " OR ",
                    guarded.Select(column =>
                        $"{ByteLength(kind, column)} > {maximumCellBytes.ToString(CultureInfo.InvariantCulture)}"))
                .Append(" THEN 1 ELSE 0 END");
        }

        sql.Append(" AS ").Append(Quote(kind, "__thub_oversize"));
        foreach (var column in outputColumns)
        {
            var source = Quote(kind, column.SourceName);
            sql.Append(", ");
            if (guarded.Any(candidate => candidate.Id == column.Id))
            {
                sql.Append("CASE WHEN ")
                    .Append(ByteLength(kind, column))
                    .Append(" <= ")
                    .Append(maximumCellBytes.ToString(CultureInfo.InvariantCulture))
                    .Append(" THEN ")
                    .Append(source)
                    .Append(" ELSE NULL END");
            }
            else
            {
                sql.Append(source);
            }

            sql.Append(" AS ").Append(Quote(kind, column.PublicAlias));
        }

        sql.Append(" FROM ")
            .Append(RelationalExecutionSupport.QualifiedName(
                kind,
                version.SourceSchema,
                version.SourceObject));
        if (where.Count > 0)
        {
            sql.Append(" WHERE ").AppendJoin(" AND ", where.Select(value => $"({value})"));
        }

        sql.Append(" ORDER BY ").AppendJoin(", ", sorts.Select(BuildOrderExpression));
        var fetch = checked(query.Take + 1).ToString(CultureInfo.InvariantCulture);
        sql.Append(kind == ConnectionKind.Oracle
            ? $" FETCH FIRST {fetch} ROWS ONLY"
            : $" LIMIT {fetch}");

        return new(
            SqlPublicationPlanStatus.Success,
            new RelationalPublicationReadPlan(
                sql.ToString(),
                parameters,
                outputColumns,
                query.Filters,
                sorts,
                query.Take));
    }

    private static bool TryBuildFilter(
        ConnectionKind kind,
        PublicationFilter filter,
        int index,
        IReadOnlyDictionary<string, PublicationColumn> columns,
        ICollection<RelationalPublicationParameter> parameters,
        out string clause)
    {
        clause = string.Empty;
        if (!Enum.IsDefined(filter.Operator) ||
            filter.Value?.Length > 4_096 ||
            !columns.TryGetValue(filter.ColumnAlias, out var column) ||
            !column.IsReadable ||
            !column.IsFilterable)
        {
            return false;
        }

        var expression = Quote(kind, column.SourceName);
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

        var name = $"__filter{index}";
        var marker = Marker(kind, name);
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
            parameters.Add(new RelationalPublicationParameter(name, pattern, column));
            clause = $"{expression} LIKE {marker} ESCAPE '~'";
            return true;
        }

        if (!SqlPublicationValueConverter.TryParse(filter.Value, column, out var parsed))
        {
            return false;
        }

        var sqlOperator = filter.Operator switch
        {
            PublicationFilterOperator.Equal => "=",
            PublicationFilterOperator.NotEqual => "<>",
            PublicationFilterOperator.GreaterThan => ">",
            PublicationFilterOperator.GreaterThanOrEqual => ">=",
            PublicationFilterOperator.LessThan => "<",
            PublicationFilterOperator.LessThanOrEqual => "<=",
            _ => null
        };
        if (sqlOperator is null)
        {
            return false;
        }

        parameters.Add(new RelationalPublicationParameter(name, parsed, column));
        clause = $"{expression} {sqlOperator} {marker}";
        return true;
    }

    private static string BuildCursorPredicate(
        ConnectionKind kind,
        IReadOnlyList<SqlPublicationSortTerm> sorts,
        IReadOnlyList<object?> values,
        ICollection<RelationalPublicationParameter> parameters)
    {
        var branches = new List<string>();
        var equal = new List<string>();
        for (var index = 0; index < sorts.Count; index++)
        {
            var sort = sorts[index];
            var value = values[index];
            var expression = sort.SqlExpression;
            string? after;
            if (value is null)
            {
                after = sort.Descending ? null : $"{expression} IS NOT NULL";
                equal.Add($"{expression} IS NULL");
            }
            else
            {
                var name = $"__cursor{index}";
                var marker = Marker(kind, name);
                parameters.Add(new RelationalPublicationParameter(name, value, sort.Column));
                after = sort.Descending
                    ? $"({expression} < {marker} OR {expression} IS NULL)"
                    : $"{expression} > {marker}";
                var prefix = equal.Count == 0 ? string.Empty : $"{string.Join(" AND ", equal)} AND ";
                branches.Add($"({prefix}{after})");
                equal.Add($"{expression} = {marker}");
                continue;
            }

            if (after is not null)
            {
                var prefix = equal.Count == 1
                    ? string.Empty
                    : $"{string.Join(" AND ", equal.Take(equal.Count - 1))} AND ";
                branches.Add($"({prefix}{after})");
            }
        }

        return branches.Count == 0 ? "1 = 0" : $"({string.Join(" OR ", branches)})";
    }

    private static string BuildOrderExpression(SqlPublicationSortTerm sort)
    {
        var nullRank = sort.Descending ? 1 : 0;
        var valueRank = sort.Descending ? 0 : 1;
        return $"CASE WHEN {sort.SqlExpression} IS NULL THEN {nullRank} ELSE {valueRank} END ASC, " +
            $"{sort.SqlExpression} {(sort.Descending ? "DESC" : "ASC")}";
    }

    private static string ByteLength(ConnectionKind kind, PublicationColumn column)
    {
        var expression = Quote(kind, column.SourceName);
        if (kind == ConnectionKind.Oracle &&
            column.SourceTypeName.Contains("LOB", StringComparison.OrdinalIgnoreCase))
        {
            return $"DBMS_LOB.GETLENGTH({expression})";
        }

        return kind switch
        {
            ConnectionKind.MySql => $"OCTET_LENGTH({expression})",
            ConnectionKind.PostgreSql => $"OCTET_LENGTH({expression})",
            ConnectionKind.Oracle => $"LENGTHB({expression})",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static string Quote(ConnectionKind kind, string identifier) =>
        RelationalExecutionSupport.Quote(kind, identifier);

    private static string Marker(ConnectionKind kind, string name) =>
        kind == ConnectionKind.Oracle ? $":{name}" : $"@{name}";

    private static string EscapeLikeValue(string value) => value
        .Replace("~", "~~", StringComparison.Ordinal)
        .Replace("%", "~%", StringComparison.Ordinal)
        .Replace("_", "~_", StringComparison.Ordinal);

    private static RelationalPublicationPlanResult InvalidQuery() =>
        new(SqlPublicationPlanStatus.InvalidQuery, null);
}
