using System.Globalization;
using System.Text;
using THub.Application.Publications;
using THub.Domain.Connections;
using THub.Domain.Publications;
using THub.Infrastructure.Execution;

namespace THub.Infrastructure.Publications;

internal sealed record RelationalPublicationLookupPlan(
    string CommandText,
    IReadOnlyList<RelationalPublicationParameter> Parameters,
    IReadOnlyList<(PublicationColumn LocalColumn, string ReferencedColumn)> KeyColumns,
    string DisplayAlias,
    IReadOnlyList<PublicationFilter> CursorFilters,
    IReadOnlyList<SqlPublicationSortTerm> Sorts,
    int Take);

internal sealed record RelationalPublicationLookupPlanResult(
    SqlPublicationPlanStatus Status,
    RelationalPublicationLookupPlan? Plan);

internal static class RelationalPublicationLookupPlanner
{
    public static RelationalPublicationLookupPlanResult Build(
        ConnectionKind kind,
        PublicationVersion version,
        PublicationColumn requestedColumn,
        PublicationForeignKeySourceQuery query)
    {
        if (requestedColumn.ForeignKey is null ||
            query.Take is < 1 or > 100 ||
            query.Search?.Length > 256)
        {
            return InvalidQuery();
        }

        var foreignKey = requestedColumn.ForeignKey;
        var keys = version.Columns
            .Where(column => string.Equals(
                column.ForeignKey?.ConstraintName,
                foreignKey.ConstraintName,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(column => column.ForeignKey!.Ordinal)
            .Select(column => (
                LocalColumn: column,
                ReferencedColumn: column.ForeignKey!.ReferencedColumn))
            .ToArray();
        if (keys.Length != foreignKey.ColumnCount ||
            foreignKey.SearchColumns.Count == 0 ||
            keys.Any(key => !key.LocalColumn.IsReadable))
        {
            return InvalidQuery();
        }

        var displayAlias = CreateDisplayAlias(version.Columns.Select(column => column.PublicAlias));
        var displayColumn = new PublicationColumn(
            Guid.NewGuid(),
            version.Id,
            1_023,
            foreignKey.DisplayColumn,
            displayAlias,
            PublicationDataType.String,
            "varchar(4000)",
            isNullable: true,
            isReadable: true,
            isFilterable: false,
            isSortable: true,
            isWritable: false,
            maximumLength: 4_000);
        var displayExpression = DisplayExpression(kind, foreignKey.DisplayColumn);
        var sorts = new List<SqlPublicationSortTerm>
        {
            new(displayColumn, false, displayExpression)
        };
        sorts.AddRange(keys.Select(key => new SqlPublicationSortTerm(
            key.LocalColumn,
            false,
            Quote(kind, key.ReferencedColumn))));
        var cursorFilters = query.Search is null
            ? Array.Empty<PublicationFilter>()
            :
            [new PublicationFilter(displayAlias, PublicationFilterOperator.Contains, query.Search)];
        var parameters = new List<RelationalPublicationParameter>();
        var where = new List<string>();
        if (query.Search is not null)
        {
            const string name = "__search";
            var escaped = EscapeLikeValue(query.Search);
            parameters.Add(new RelationalPublicationParameter(
                name,
                $"%{escaped}%",
                displayColumn));
            where.Add($"({string.Join(
                " OR ",
                foreignKey.SearchColumns.Select(column =>
                    $"{DisplayExpression(kind, column)} LIKE {Marker(kind, name)} ESCAPE '~'"))})");
        }

        if (query.Cursor is not null)
        {
            if (!SqlPublicationCursorCodec.TryDecode(
                    query.Cursor,
                    version,
                    cursorFilters,
                    sorts,
                    out var values))
            {
                return new(SqlPublicationPlanStatus.InvalidCursor, null);
            }

            where.Add(BuildCursorPredicate(kind, sorts, values, parameters));
        }

        var sql = new StringBuilder("SELECT ")
            .Append(displayExpression)
            .Append(" AS ")
            .Append(Quote(kind, displayAlias));
        foreach (var key in keys)
        {
            sql.Append(", ")
                .Append(Quote(kind, key.ReferencedColumn))
                .Append(" AS ")
                .Append(Quote(kind, key.LocalColumn.PublicAlias));
        }

        sql.Append(" FROM ")
            .Append(RelationalExecutionSupport.QualifiedName(
                kind,
                foreignKey.ReferencedSchema,
                foreignKey.ReferencedObject));
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
            new RelationalPublicationLookupPlan(
                sql.ToString(),
                parameters,
                keys,
                displayAlias,
                cursorFilters,
                sorts,
                query.Take));
    }

    public static string DisplayExpression(ConnectionKind kind, string column)
    {
        var quoted = Quote(kind, column);
        return kind switch
        {
            ConnectionKind.MySql => $"CAST({quoted} AS CHAR(4000))",
            ConnectionKind.PostgreSql => $"CAST({quoted} AS text)",
            ConnectionKind.Oracle => $"CAST({quoted} AS VARCHAR2(4000))",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    public static string Marker(ConnectionKind kind, string name) =>
        kind == ConnectionKind.Oracle ? $":{name}" : $"@{name}";

    public static string Quote(ConnectionKind kind, string identifier) =>
        RelationalExecutionSupport.Quote(kind, identifier);

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
                after = $"{expression} IS NOT NULL";
                equal.Add($"{expression} IS NULL");
            }
            else
            {
                var name = $"__cursor{index}";
                var marker = Marker(kind, name);
                parameters.Add(new RelationalPublicationParameter(name, value, sort.Column));
                after = $"{expression} > {marker}";
                var prefix = equal.Count == 0 ? string.Empty : $"{string.Join(" AND ", equal)} AND ";
                branches.Add($"({prefix}{after})");
                equal.Add($"{expression} = {marker}");
                continue;
            }

            var nullPrefix = equal.Count == 1
                ? string.Empty
                : $"{string.Join(" AND ", equal.Take(equal.Count - 1))} AND ";
            branches.Add($"({nullPrefix}{after})");
        }

        return branches.Count == 0 ? "1 = 0" : $"({string.Join(" OR ", branches)})";
    }

    private static string BuildOrderExpression(SqlPublicationSortTerm sort) =>
        $"CASE WHEN {sort.SqlExpression} IS NULL THEN 0 ELSE 1 END ASC, {sort.SqlExpression} ASC";

    private static string CreateDisplayAlias(IEnumerable<string> existingAliases)
    {
        var existing = existingAliases.ToHashSet(StringComparer.OrdinalIgnoreCase);
        const string prefix = "thubLookupDisplay";
        if (!existing.Contains(prefix))
        {
            return prefix;
        }

        for (var suffix = 2; suffix < 100; suffix++)
        {
            var candidate = $"{prefix}{suffix}";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("A unique internal lookup alias could not be generated.");
    }

    private static string EscapeLikeValue(string value) => value
        .Replace("~", "~~", StringComparison.Ordinal)
        .Replace("%", "~%", StringComparison.Ordinal)
        .Replace("_", "~_", StringComparison.Ordinal);

    private static RelationalPublicationLookupPlanResult InvalidQuery() =>
        new(SqlPublicationPlanStatus.InvalidQuery, null);
}
