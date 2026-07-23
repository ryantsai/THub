using System.Globalization;
using System.Text;
using THub.Application.Execution;
using THub.Domain.Connections;

namespace THub.Infrastructure.Execution;

internal static class RelationalTargetMutationSql
{
    public static string Build(
        ConnectionKind kind,
        string schema,
        string objectName,
        string mode,
        IReadOnlyList<string> mappedColumns,
        IReadOnlyList<string> keyColumns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        ArgumentNullException.ThrowIfNull(mappedColumns);
        ArgumentNullException.ThrowIfNull(keyColumns);

        var qualifiedName = $"{Quote(kind, schema)}.{Quote(kind, objectName)}";
        var markers = mappedColumns
            .Select((_, index) => Marker(kind, index))
            .ToArray();
        if (mode == "insert")
        {
            return Insert(qualifiedName, kind, mappedColumns, markers);
        }

        var keyIndexes = keyColumns.Select(key =>
        {
            var index = IndexOf(mappedColumns, key);
            return index >= 0
                ? index
                : throw new ArgumentException(
                    $"Key column '{key}' is not mapped.",
                    nameof(keyColumns));
        }).ToArray();
        var predicate = string.Join(
            " AND ",
            keyIndexes.Select(index =>
                $"{Quote(kind, mappedColumns[index])} = {markers[index]}"));
        if (mode == "delete")
        {
            return $"DELETE FROM {qualifiedName} WHERE {predicate}";
        }
        if (mode != "upsert")
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported target mode.");
        }

        var keySet = keyColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updateIndexes = mappedColumns
            .Select((column, index) => (column, index))
            .Where(item => !keySet.Contains(item.column))
            .Select(item => item.index)
            .ToArray();
        if (updateIndexes.Length == 0)
        {
            throw new ArgumentException(
                "Upsert requires at least one mapped non-key column.",
                nameof(mappedColumns));
        }

        return kind switch
        {
            ConnectionKind.SqlServer => BuildSqlServerUpsert(
                qualifiedName,
                mappedColumns,
                markers,
                predicate,
                updateIndexes),
            ConnectionKind.MySql => BuildMySqlUpsert(
                qualifiedName,
                mappedColumns,
                markers,
                updateIndexes),
            ConnectionKind.PostgreSql => BuildPostgreSqlUpsert(
                qualifiedName,
                mappedColumns,
                markers,
                keyColumns,
                updateIndexes),
            ConnectionKind.Oracle => BuildOracleUpsert(
                qualifiedName,
                mappedColumns,
                markers,
                keyColumns,
                updateIndexes),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported database kind.")
        };
    }

    private static string BuildSqlServerUpsert(
        string qualifiedName,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> markers,
        string predicate,
        IReadOnlyList<int> updateIndexes)
    {
        var updates = string.Join(
            ", ",
            updateIndexes.Select(index =>
                $"{Quote(ConnectionKind.SqlServer, columns[index])} = {markers[index]}"));
        return
            $"UPDATE {qualifiedName} WITH (UPDLOCK, SERIALIZABLE) SET {updates} WHERE {predicate}; " +
            $"IF @@ROWCOUNT = 0 BEGIN {Insert(qualifiedName, ConnectionKind.SqlServer, columns, markers)}; END";
    }

    private static string BuildMySqlUpsert(
        string qualifiedName,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> markers,
        IReadOnlyList<int> updateIndexes)
    {
        var updates = string.Join(
            ", ",
            updateIndexes.Select(index =>
                $"{Quote(ConnectionKind.MySql, columns[index])} = VALUES({Quote(ConnectionKind.MySql, columns[index])})"));
        return
            $"{Insert(qualifiedName, ConnectionKind.MySql, columns, markers)} " +
            $"ON DUPLICATE KEY UPDATE {updates}";
    }

    private static string BuildPostgreSqlUpsert(
        string qualifiedName,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> markers,
        IReadOnlyList<string> keys,
        IReadOnlyList<int> updateIndexes)
    {
        var conflict = string.Join(
            ", ",
            keys.Select(key => Quote(ConnectionKind.PostgreSql, key)));
        var updates = string.Join(
            ", ",
            updateIndexes.Select(index =>
                $"{Quote(ConnectionKind.PostgreSql, columns[index])} = EXCLUDED.{Quote(ConnectionKind.PostgreSql, columns[index])}"));
        return
            $"{Insert(qualifiedName, ConnectionKind.PostgreSql, columns, markers)} " +
            $"ON CONFLICT ({conflict}) DO UPDATE SET {updates}";
    }

    private static string BuildOracleUpsert(
        string qualifiedName,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> markers,
        IReadOnlyList<string> keys,
        IReadOnlyList<int> updateIndexes)
    {
        var sourceProjection = string.Join(
            ", ",
            columns.Select((column, index) =>
                $"{markers[index]} AS {Quote(ConnectionKind.Oracle, column)}"));
        var predicate = string.Join(
            " AND ",
            keys.Select(key =>
                $"target.{Quote(ConnectionKind.Oracle, key)} = source.{Quote(ConnectionKind.Oracle, key)}"));
        var updates = string.Join(
            ", ",
            updateIndexes.Select(index =>
                $"target.{Quote(ConnectionKind.Oracle, columns[index])} = source.{Quote(ConnectionKind.Oracle, columns[index])}"));
        var insertColumns = string.Join(
            ", ",
            columns.Select(column => Quote(ConnectionKind.Oracle, column)));
        var insertValues = string.Join(
            ", ",
            columns.Select(column => $"source.{Quote(ConnectionKind.Oracle, column)}"));
        return
            $"MERGE INTO {qualifiedName} target USING (SELECT {sourceProjection} FROM DUAL) source " +
            $"ON ({predicate}) WHEN MATCHED THEN UPDATE SET {updates} " +
            $"WHEN NOT MATCHED THEN INSERT ({insertColumns}) VALUES ({insertValues})";
    }

    private static string Insert(
        string qualifiedName,
        ConnectionKind kind,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> markers) =>
        $"INSERT INTO {qualifiedName} ({string.Join(", ", columns.Select(column => Quote(kind, column)))}) " +
        $"VALUES ({string.Join(", ", markers)})";

    private static string Marker(ConnectionKind kind, int index) =>
        kind == ConnectionKind.Oracle ? $":p{index}" : $"@p{index}";

    private static string Quote(ConnectionKind kind, string identifier) => kind switch
    {
        ConnectionKind.SqlServer => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]",
        ConnectionKind.MySql => $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`",
        ConnectionKind.PostgreSql or ConnectionKind.Oracle =>
            $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported database kind.")
    };

    private static int IndexOf(IReadOnlyList<string> columns, string expected)
    {
        for (var index = 0; index < columns.Count; index++)
        {
            if (string.Equals(columns[index], expected, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }
        return -1;
    }
}

internal sealed class RelationalMutationKeyTracker
{
    private readonly HashSet<string> encountered = new(StringComparer.Ordinal);

    public void Add(IReadOnlyList<TabularValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0 || values.Any(value => value.Kind == TabularValueKind.Null))
        {
            throw ExecutionFailure.Data(
                "execution.database.target.key.null",
                "Target mutation keys must be complete and non-null.");
        }

        var builder = new StringBuilder();
        foreach (var value in values)
        {
            var text = Format(value);
            _ = builder
                .Append((int)value.Kind)
                .Append(':')
                .Append(text.Length)
                .Append(':')
                .Append(text)
                .Append(';');
        }

        if (!encountered.Add(builder.ToString()))
        {
            throw ExecutionFailure.Data(
                "execution.database.target.key.duplicate",
                "The input contains the same target mutation key more than once.");
        }
    }

    private static string Format(TabularValue value) => value.Kind switch
    {
        TabularValueKind.Boolean => ((bool)value.Value!).ToString(CultureInfo.InvariantCulture),
        TabularValueKind.Int64 => ((long)value.Value!).ToString(CultureInfo.InvariantCulture),
        TabularValueKind.Decimal => ((decimal)value.Value!).ToString(CultureInfo.InvariantCulture),
        TabularValueKind.Double => ((double)value.Value!).ToString("R", CultureInfo.InvariantCulture),
        TabularValueKind.String => (string)value.Value!,
        TabularValueKind.DateTimeOffset =>
            ((DateTimeOffset)value.Value!).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        TabularValueKind.Guid => ((Guid)value.Value!).ToString("D"),
        TabularValueKind.Binary => Convert.ToBase64String(((ReadOnlyMemory<byte>)value.Value!).Span),
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}
