using System.Data;
using System.Data.Common;
using System.Text.Json;
using THub.Application.Publications;
using THub.Domain.Connections;
using THub.Domain.Publications;
using THub.Infrastructure.Execution;

namespace THub.Infrastructure.Publications;

internal static class RelationalPublicationMutationBuilder
{
    public static string Build(
        DbCommand command,
        ConnectionKind kind,
        PublicationVersion version,
        PublicationChange change)
    {
        var columns = version.Columns.ToDictionary(
            column => column.PublicAlias,
            StringComparer.OrdinalIgnoreCase);
        var key = ParseObject(change.KeyJson, PublicationChange.MaximumKeyJsonLength);
        var before = ParseObject(change.BeforeJson, PublicationChange.MaximumRowJsonLength);
        var after = ParseObject(change.AfterJson, PublicationChange.MaximumRowJsonLength);
        return change.Operation switch
        {
            PublicationChangeOperation.Insert => BuildInsert(command, kind, version, columns, after),
            PublicationChangeOperation.Update =>
                BuildUpdate(command, kind, version, columns, key, before, after),
            PublicationChangeOperation.Delete =>
                BuildDelete(command, kind, version, columns, key, before),
            _ => throw new InvalidOperationException("The staged change operation is unsupported.")
        };
    }

    private static string BuildInsert(
        DbCommand command,
        ConnectionKind kind,
        PublicationVersion version,
        IReadOnlyDictionary<string, PublicationColumn> columns,
        IReadOnlyDictionary<string, JsonElement> after)
    {
        if (after.Count == 0 || after.Any(value =>
                !columns.TryGetValue(value.Key, out var column) ||
                !PublicationColumnMutationPolicy.CanSupplyOnInsert(column)))
        {
            throw new InvalidOperationException(
                "Insert values contain a non-insertable publication alias.");
        }

        var required = version.Columns
            .Where(column =>
                PublicationColumnMutationPolicy.CanSupplyOnInsert(column) &&
                !column.IsNullable)
            .Select(column => column.PublicAlias)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!required.IsSubsetOf(after.Keys))
        {
            throw new InvalidOperationException(
                "Insert values omit a required insertable column.");
        }

        var ordered = after
            .Select(value => (Column: columns[value.Key], Value: value.Value))
            .OrderBy(value => value.Column.Ordinal)
            .ToArray();
        var markers = new List<string>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var name = $"value{index}";
            AddJsonParameter(command, name, ordered[index].Value, ordered[index].Column);
            markers.Add(Marker(kind, name));
        }

        return $"INSERT INTO {QualifiedName(kind, version)} " +
            $"({string.Join(", ", ordered.Select(value => Quote(kind, value.Column.SourceName)))}) " +
            $"VALUES ({string.Join(", ", markers)})";
    }

    private static string BuildUpdate(
        DbCommand command,
        ConnectionKind kind,
        PublicationVersion version,
        IReadOnlyDictionary<string, PublicationColumn> columns,
        IReadOnlyDictionary<string, JsonElement> key,
        IReadOnlyDictionary<string, JsonElement> before,
        IReadOnlyDictionary<string, JsonElement> after)
    {
        if (after.Count == 0 || after.Any(value =>
                !columns.TryGetValue(value.Key, out var column) ||
                !PublicationColumnMutationPolicy.CanSetOnUpdate(column)))
        {
            throw new InvalidOperationException(
                "Update values contain a non-mutable or key publication alias.");
        }

        if (version.ConcurrencyMode != PublicationConcurrencyMode.OriginalValues)
        {
            throw new InvalidOperationException(
                "Non-SQL Server editor publications require original-value concurrency.");
        }

        if (after.Keys.Any(alias => !before.ContainsKey(alias)))
        {
            throw new InvalidOperationException(
                "Original values are missing for an updated column.");
        }

        var assignments = new List<string>();
        foreach (var value in after.OrderBy(value => columns[value.Key].Ordinal))
        {
            var column = columns[value.Key];
            var name = $"set{assignments.Count}";
            AddJsonParameter(command, name, value.Value, column);
            assignments.Add($"{Quote(kind, column.SourceName)} = {Marker(kind, name)}");
        }

        var predicates = BuildPredicates(command, kind, version, columns, key, before);
        return $"UPDATE {QualifiedName(kind, version)} SET {string.Join(", ", assignments)} " +
            $"WHERE {string.Join(" AND ", predicates)}";
    }

    private static string BuildDelete(
        DbCommand command,
        ConnectionKind kind,
        PublicationVersion version,
        IReadOnlyDictionary<string, PublicationColumn> columns,
        IReadOnlyDictionary<string, JsonElement> key,
        IReadOnlyDictionary<string, JsonElement> before)
    {
        if (version.ConcurrencyMode != PublicationConcurrencyMode.OriginalValues)
        {
            throw new InvalidOperationException(
                "Non-SQL Server editor publications require original-value concurrency.");
        }

        if (version.Columns
            .Where(column => column.IsWritable)
            .Any(column => !before.ContainsKey(column.PublicAlias)))
        {
            throw new InvalidOperationException(
                "Original values are incomplete for a delete operation.");
        }

        var predicates = BuildPredicates(command, kind, version, columns, key, before);
        return $"DELETE FROM {QualifiedName(kind, version)} " +
            $"WHERE {string.Join(" AND ", predicates)}";
    }

    private static IReadOnlyList<string> BuildPredicates(
        DbCommand command,
        ConnectionKind kind,
        PublicationVersion version,
        IReadOnlyDictionary<string, PublicationColumn> columns,
        IReadOnlyDictionary<string, JsonElement> key,
        IReadOnlyDictionary<string, JsonElement> before)
    {
        var keyColumns = version.Columns
            .Where(column => column.IsKey)
            .OrderBy(column => column.KeyOrdinal)
            .ToArray();
        if (key.Count != keyColumns.Length ||
            keyColumns.Any(column => !key.ContainsKey(column.PublicAlias)))
        {
            throw new InvalidOperationException(
                "The staged key does not match the active publication key.");
        }

        var values = new List<(PublicationColumn Column, JsonElement Value)>();
        values.AddRange(keyColumns.Select(column => (column, key[column.PublicAlias])));
        foreach (var value in before.OrderBy(value =>
                     columns.TryGetValue(value.Key, out var column)
                         ? column.Ordinal
                         : int.MaxValue))
        {
            if (!columns.TryGetValue(value.Key, out var column) || !column.IsReadable)
            {
                throw new InvalidOperationException(
                    "Original values contain an unapproved publication alias.");
            }

            if (values.All(existing => !string.Equals(
                    existing.Column.PublicAlias,
                    column.PublicAlias,
                    StringComparison.OrdinalIgnoreCase)))
            {
                values.Add((column, value.Value));
            }
        }

        var predicates = new List<string>(values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            var identifier = Quote(kind, value.Column.SourceName);
            if (value.Value.ValueKind == JsonValueKind.Null)
            {
                if (!value.Column.IsNullable)
                {
                    throw new InvalidOperationException(
                        "A non-nullable predicate value cannot be null.");
                }

                predicates.Add($"{identifier} IS NULL");
                continue;
            }

            var name = $"where{index}";
            AddJsonParameter(command, name, value.Value, value.Column);
            predicates.Add($"{identifier} = {Marker(kind, name)}");
        }

        return predicates;
    }

    private static void AddJsonParameter(
        DbCommand command,
        string name,
        JsonElement value,
        PublicationColumn column)
    {
        object? parsed;
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (!column.IsNullable)
            {
                throw new InvalidOperationException(
                    "A non-nullable publication value cannot be null.");
            }

            parsed = null;
        }
        else
        {
            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString()!,
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => throw new InvalidOperationException(
                    "Publication values must be scalar JSON values.")
            };
            if (!SqlPublicationValueConverter.TryParse(text, column, out parsed))
            {
                throw new InvalidOperationException(
                    "A publication value is incompatible with its immutable column type.");
            }
        }

        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = MapDbType(column.DataType);
        parameter.IsNullable = column.IsNullable;
        parameter.Value = parsed ?? DBNull.Value;
        if (column.DataType is PublicationDataType.String or PublicationDataType.Binary &&
            column.MaximumLength is int maximumLength)
        {
            parameter.Size = maximumLength;
        }
        else if (column.DataType == PublicationDataType.Decimal)
        {
            parameter.Precision = column.NumericPrecision ?? 38;
            parameter.Scale = column.NumericScale ?? 0;
        }

        command.Parameters.Add(parameter);
    }

    private static IReadOnlyDictionary<string, JsonElement> ParseObject(
        string? json,
        int maximumLength)
    {
        if (json is null)
        {
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        }

        if (json.Length > maximumLength)
        {
            throw new InvalidOperationException("A staged JSON value exceeds its bound.");
        }

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("A staged value must be a JSON object.");
        }

        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!values.TryAdd(property.Name, property.Value.Clone()))
            {
                throw new InvalidOperationException(
                    "A staged JSON object contains duplicate aliases.");
            }
        }

        return values;
    }

    private static string QualifiedName(ConnectionKind kind, PublicationVersion version) =>
        RelationalExecutionSupport.QualifiedName(kind, version.SourceSchema, version.SourceObject);

    private static string Quote(ConnectionKind kind, string identifier) =>
        RelationalExecutionSupport.Quote(kind, identifier);

    private static string Marker(ConnectionKind kind, string name) =>
        kind == ConnectionKind.Oracle ? $":{name}" : $"@{name}";

    private static DbType MapDbType(PublicationDataType type) => type switch
    {
        PublicationDataType.Boolean => DbType.Boolean,
        PublicationDataType.Byte => DbType.Byte,
        PublicationDataType.Int16 => DbType.Int16,
        PublicationDataType.Int32 => DbType.Int32,
        PublicationDataType.Int64 => DbType.Int64,
        PublicationDataType.Decimal => DbType.Decimal,
        PublicationDataType.Single => DbType.Single,
        PublicationDataType.Double => DbType.Double,
        PublicationDataType.Date => DbType.Date,
        PublicationDataType.DateTime => DbType.DateTime,
        PublicationDataType.DateTimeOffset => DbType.DateTimeOffset,
        PublicationDataType.Time => DbType.Time,
        PublicationDataType.Guid => DbType.Guid,
        PublicationDataType.String => DbType.String,
        PublicationDataType.Binary => DbType.Binary,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
}
