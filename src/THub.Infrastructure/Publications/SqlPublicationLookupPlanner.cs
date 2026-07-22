using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;
using THub.Application.Publications;
using THub.Domain.Publications;

namespace THub.Infrastructure.Publications;

internal sealed record SqlPublicationLookupPlan(
    string CommandText,
    IReadOnlyList<SqlParameter> Parameters,
    IReadOnlyList<(PublicationColumn LocalColumn, string ReferencedColumn)> KeyColumns,
    string DisplayAlias,
    IReadOnlyList<PublicationFilter> CursorFilters,
    IReadOnlyList<SqlPublicationSortTerm> Sorts,
    int Take);

internal sealed record SqlPublicationLookupPlanResult(
    SqlPublicationPlanStatus Status,
    SqlPublicationLookupPlan? Plan);

internal static class SqlPublicationLookupPlanner
{
    public static SqlPublicationLookupPlanResult Build(
        PublicationVersion version,
        PublicationColumn requestedColumn,
        PublicationForeignKeySourceQuery query)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(requestedColumn);
        ArgumentNullException.ThrowIfNull(query);
        if (requestedColumn.ForeignKey is null ||
            query.Take is < 1 or > 100 ||
            query.Search?.Length > 256)
        {
            return InvalidQuery();
        }

        var requestedForeignKey = requestedColumn.ForeignKey;
        var keyColumns = version.Columns
            .Where(column => string.Equals(
                column.ForeignKey?.ConstraintName,
                requestedForeignKey.ConstraintName,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(column => column.ForeignKey!.Ordinal)
            .Select(column => (LocalColumn: column, ReferencedColumn: column.ForeignKey!.ReferencedColumn))
            .ToArray();
        if (keyColumns.Length != requestedForeignKey.ColumnCount)
        {
            return InvalidQuery();
        }

        if (requestedForeignKey.SearchColumns.Count == 0 ||
            keyColumns.Any(key => !key.LocalColumn.IsReadable) ||
            keyColumns.Any(key =>
                key.LocalColumn.ForeignKey is null ||
                !string.Equals(key.LocalColumn.ForeignKey.ReferencedSchema, requestedForeignKey.ReferencedSchema, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(key.LocalColumn.ForeignKey.ReferencedObject, requestedForeignKey.ReferencedObject, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(key.LocalColumn.ForeignKey.DisplayColumn, requestedForeignKey.DisplayColumn, StringComparison.OrdinalIgnoreCase) ||
                !key.LocalColumn.ForeignKey.SearchColumns.SequenceEqual(requestedForeignKey.SearchColumns, StringComparer.OrdinalIgnoreCase) ||
                key.LocalColumn.ForeignKey.LookupMode != requestedForeignKey.LookupMode) ||
            (requestedForeignKey.IsComposite &&
             (keyColumns.Select(key => key.LocalColumn.IsWritable).Distinct().Count() != 1 ||
              keyColumns.Select(key => key.LocalColumn.IsNullable).Distinct().Count() != 1)))
        {
            return InvalidQuery();
        }

        var displayAlias = CreateDisplayAlias(version.Columns.Select(column => column.PublicAlias));
        var displayColumn = new PublicationColumn(
            Guid.NewGuid(),
            version.Id,
            1_023,
            requestedForeignKey.DisplayColumn,
            displayAlias,
            PublicationDataType.String,
            "nvarchar(4000)",
            isNullable: true,
            isReadable: true,
            isFilterable: false,
            isSortable: true,
            isWritable: false,
            maximumLength: 4_000);
        var displayExpression =
            $"CONVERT(nvarchar(4000), {SqlPublicationQueryPlanner.QuoteIdentifier(requestedForeignKey.DisplayColumn)})";
        var sorts = new List<SqlPublicationSortTerm>
        {
            new(displayColumn, false, displayExpression),
        };
        sorts.AddRange(keyColumns.Select(key => new SqlPublicationSortTerm(
            key.LocalColumn,
            false,
            SqlPublicationQueryPlanner.QuoteIdentifier(key.ReferencedColumn))));

        var cursorFilters = query.Search is null
            ? Array.Empty<PublicationFilter>()
            :
            [new PublicationFilter(displayAlias, PublicationFilterOperator.Contains, query.Search)];
        var parameters = new List<SqlParameter>
        {
            new("@__take", SqlDbType.Int) { Value = checked(query.Take + 1) },
        };
        var whereParts = new List<string>();
        if (query.Search is not null)
        {
            var pattern = $"%{SqlPublicationQueryPlanner.EscapeLikeValue(query.Search)}%";
            parameters.Add(new SqlParameter("@__search", SqlDbType.NVarChar, 514)
            {
                Value = pattern,
            });
            whereParts.Add($"({string.Join(
                " OR ",
                requestedForeignKey.SearchColumns.Select(searchColumn =>
                    $"CONVERT(nvarchar(4000), {SqlPublicationQueryPlanner.QuoteIdentifier(searchColumn)}) LIKE @__search ESCAPE N'~'"))})");
        }

        if (query.Cursor is not null)
        {
            if (!SqlPublicationCursorCodec.TryDecode(
                    query.Cursor,
                    version,
                    cursorFilters,
                    sorts,
                    out var cursorValues))
            {
                return new SqlPublicationLookupPlanResult(SqlPublicationPlanStatus.InvalidCursor, null);
            }

            whereParts.Add(SqlPublicationQueryPlanner.BuildCursorPredicate(
                sorts,
                cursorValues,
                parameters));
        }

        var guardedKeys = keyColumns
            .Where(key => key.LocalColumn.DataType is PublicationDataType.String or PublicationDataType.Binary)
            .ToArray();
        if (guardedKeys.Length > 0)
        {
            parameters.Add(new SqlParameter("@__maxCellBytes", SqlDbType.Int)
            {
                Value = Math.Min(2 * 1_024 * 1_024, version.Settings.MaximumResponseBytes),
            });
        }

        var sql = new StringBuilder("SELECT TOP (@__take) ");
        if (guardedKeys.Length == 0)
        {
            sql.Append("CAST(0 AS bit)");
        }
        else
        {
            sql.Append("CAST(CASE WHEN ").AppendJoin(
                " OR ",
                guardedKeys.Select(key =>
                    $"DATALENGTH({SqlPublicationQueryPlanner.QuoteIdentifier(key.ReferencedColumn)}) > @__maxCellBytes"))
                .Append(" THEN 1 ELSE 0 END AS bit)");
        }

        sql.Append(" AS [__thub_oversize], ")
            .Append(displayExpression)
            .Append(" AS ")
            .Append(SqlPublicationQueryPlanner.QuoteIdentifier(displayAlias));
        foreach (var key in keyColumns)
        {
            var source = SqlPublicationQueryPlanner.QuoteIdentifier(key.ReferencedColumn);
            sql.Append(", ");
            if (guardedKeys.Any(candidate => candidate.LocalColumn.Id == key.LocalColumn.Id))
            {
                sql.Append("CASE WHEN DATALENGTH(")
                    .Append(source)
                    .Append(") <= @__maxCellBytes THEN ")
                    .Append(source)
                    .Append(" ELSE NULL END");
            }
            else
            {
                sql.Append(source);
            }

            sql.Append(" AS ").Append(SqlPublicationQueryPlanner.QuoteIdentifier(key.LocalColumn.PublicAlias));
        }

        sql.Append(" FROM ")
            .Append(SqlPublicationQueryPlanner.QuoteIdentifier(requestedForeignKey.ReferencedSchema))
            .Append('.')
            .Append(SqlPublicationQueryPlanner.QuoteIdentifier(requestedForeignKey.ReferencedObject));
        if (whereParts.Count > 0)
        {
            sql.Append(" WHERE ").AppendJoin(" AND ", whereParts.Select(part => $"({part})"));
        }

        sql.Append(" ORDER BY ")
            .AppendJoin(
                ", ",
                sorts.Select(sort => $"{sort.SqlExpression} ASC"))
            .Append(';');

        return new SqlPublicationLookupPlanResult(
            SqlPublicationPlanStatus.Success,
            new SqlPublicationLookupPlan(
                sql.ToString(),
                parameters,
                keyColumns,
                displayAlias,
                cursorFilters,
                sorts,
                query.Take));
    }

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

    private static SqlPublicationLookupPlanResult InvalidQuery() =>
        new(SqlPublicationPlanStatus.InvalidQuery, null);
}
