using System.Globalization;
using System.Text.Json;
using THub.Application.Publications;
using THub.Domain.Publications;

namespace THub.Web.Publications;

public sealed record PublicationSpreadsheetRow(
    IReadOnlyDictionary<string, object?>? OriginalValues,
    IReadOnlyDictionary<string, object?> CurrentValues,
    bool IsNew,
    bool IsDeleted);

public sealed record PublicationSpreadsheetChangeMapResult(
    IReadOnlyList<StagePublicationChangeCommand> Changes,
    string? Error)
{
    public bool IsSuccess => Error is null;
}

/// <summary>
/// Converts the UI workbook snapshot into the bounded, typed change contract accepted by Application.
/// Spreadsheet text is never forwarded directly to SQL.
/// </summary>
public static class PublicationSpreadsheetChangeMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    public static PublicationSpreadsheetChangeMapResult Build(
        PublicationVersionDto version,
        IReadOnlyList<PublicationSpreadsheetRow> rows)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(rows);

        var columns = version.Columns
            .Where(column => column.IsReadable)
            .OrderBy(column => column.Ordinal)
            .ToArray();
        var insertable = columns.Where(CanSupplyOnInsert).ToArray();
        var updateable = columns.Where(CanSetOnUpdate).ToArray();
        var keys = columns.Where(column => column.IsKey).OrderBy(column => column.KeyOrdinal).ToArray();
        var changes = new List<StagePublicationChangeCommand>();

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row.IsNew && row.IsDeleted)
            {
                continue;
            }

            if (row.IsNew)
            {
                var after = NormalizeObject(row.CurrentValues, insertable, rowIndex, out var error);
                if (error is not null)
                {
                    return Failure(error);
                }

                changes.Add(new StagePublicationChangeCommand(
                    PublicationChangeOperation.Insert,
                    null,
                    null,
                    Serialize(after!)));
                continue;
            }

            if (row.OriginalValues is null)
            {
                return Failure($"Row {rowIndex + 1} is missing its original concurrency snapshot.");
            }

            var original = NormalizeObject(row.OriginalValues, columns, rowIndex, out var originalError);
            if (originalError is not null)
            {
                return Failure(originalError);
            }

            var key = keys.ToDictionary(
                column => column.PublicAlias,
                column => original![column.PublicAlias],
                StringComparer.Ordinal);
            if (row.IsDeleted)
            {
                changes.Add(new StagePublicationChangeCommand(
                    PublicationChangeOperation.Delete,
                    Serialize(key),
                    Serialize(original!),
                    null));
                continue;
            }

            var current = NormalizeObject(row.CurrentValues, updateable, rowIndex, out var currentError);
            if (currentError is not null)
            {
                return Failure(currentError);
            }

            var updated = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var column in updateable)
            {
                var before = original![column.PublicAlias];
                var after = current![column.PublicAlias];
                if (!Equivalent(before, after))
                {
                    updated.Add(column.PublicAlias, after);
                }
            }

            foreach (var group in updateable
                         .Where(column => column.ForeignKey is not null)
                         .GroupBy(column => column.ForeignKey!.ConstraintName, StringComparer.OrdinalIgnoreCase))
            {
                var components = group.ToArray();
                if (components.Any(column => updated.ContainsKey(column.PublicAlias)))
                {
                    foreach (var component in components)
                    {
                        updated[component.PublicAlias] = current![component.PublicAlias];
                    }
                }
            }

            if (updated.Count > 0)
            {
                changes.Add(new StagePublicationChangeCommand(
                    PublicationChangeOperation.Update,
                    Serialize(key),
                    Serialize(original!),
                    Serialize(updated)));
            }
        }

        return new PublicationSpreadsheetChangeMapResult(changes, null);
    }

    public static object? ToSpreadsheetValue(object? value, PublicationColumnDto column)
    {
        if (value is null)
        {
            return null;
        }

        return column.DataType switch
        {
            PublicationDataType.Binary when value is byte[] bytes => Convert.ToBase64String(bytes),
            PublicationDataType.Date when value is DateOnly date => date.ToDateTime(TimeOnly.MinValue),
            PublicationDataType.Time when value is TimeOnly time => time.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            PublicationDataType.DateTimeOffset when value is DateTimeOffset offset => offset.ToString("O", CultureInfo.InvariantCulture),
            _ => value,
        };
    }

    public static bool CanSupplyOnInsert(PublicationColumnDto column) =>
        !column.IsGenerated &&
        !column.IsConcurrencyToken &&
        (column.IsWritable || column.IsKey);

    public static bool CanSetOnUpdate(PublicationColumnDto column) =>
        column.IsWritable &&
        !column.IsKey &&
        !column.IsGenerated &&
        !column.IsConcurrencyToken;

    private static Dictionary<string, object?>? NormalizeObject(
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyList<PublicationColumnDto> columns,
        int rowIndex,
        out string? error)
    {
        var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            values.TryGetValue(column.PublicAlias, out var value);
            if (!TryNormalize(value, column, out var typed))
            {
                error = $"Row {rowIndex + 1}, column '{column.PublicAlias}' is not a valid {column.DataType} value.";
                return null;
            }

            normalized.Add(column.PublicAlias, typed);
        }

        error = null;
        return normalized;
    }

    private static bool TryNormalize(
        object? value,
        PublicationColumnDto column,
        out object? normalized)
    {
        if (value is null || value is DBNull || value is string { Length: 0 } && column.DataType != PublicationDataType.String)
        {
            normalized = null;
            return column.IsNullable;
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        switch (column.DataType)
        {
            case PublicationDataType.Boolean:
                if (value is bool boolean || bool.TryParse(text, out boolean))
                {
                    normalized = boolean;
                    return true;
                }
                break;
            case PublicationDataType.Byte:
                if (value is byte byteValue || byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out byteValue))
                {
                    normalized = byteValue;
                    return true;
                }
                break;
            case PublicationDataType.Int16:
                if (value is short shortValue || short.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out shortValue))
                {
                    normalized = shortValue;
                    return true;
                }
                break;
            case PublicationDataType.Int32:
                if (value is int intValue || int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
                {
                    normalized = intValue;
                    return true;
                }
                break;
            case PublicationDataType.Int64:
                if (value is long longValue || long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out longValue))
                {
                    normalized = longValue;
                    return true;
                }
                break;
            case PublicationDataType.Decimal:
                if (value is decimal decimalValue || decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimalValue))
                {
                    normalized = decimalValue;
                    return true;
                }
                break;
            case PublicationDataType.Single:
                if ((value is float singleValue || float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out singleValue)) && float.IsFinite(singleValue))
                {
                    normalized = singleValue;
                    return true;
                }
                break;
            case PublicationDataType.Double:
                if ((value is double doubleValue || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out doubleValue)) && double.IsFinite(doubleValue))
                {
                    normalized = doubleValue;
                    return true;
                }
                break;
            case PublicationDataType.Date:
                if (value is DateOnly dateOnly)
                {
                    normalized = dateOnly;
                    return true;
                }
                if (value is DateTime dateTime)
                {
                    normalized = DateOnly.FromDateTime(dateTime);
                    return true;
                }
                if (DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out dateOnly))
                {
                    normalized = dateOnly;
                    return true;
                }
                break;
            case PublicationDataType.DateTime:
                if (value is DateTime timestamp)
                {
                    normalized = timestamp;
                    return true;
                }
                if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind, out timestamp))
                {
                    normalized = timestamp;
                    return true;
                }
                break;
            case PublicationDataType.DateTimeOffset:
                if (value is DateTimeOffset offset)
                {
                    normalized = offset;
                    return true;
                }
                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind, out offset))
                {
                    normalized = offset;
                    return true;
                }
                break;
            case PublicationDataType.Time:
                if (value is TimeOnly timeOnly || TimeOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out timeOnly))
                {
                    normalized = timeOnly;
                    return true;
                }
                break;
            case PublicationDataType.Guid:
                if (value is Guid guid || Guid.TryParse(text, out guid))
                {
                    normalized = guid;
                    return true;
                }
                break;
            case PublicationDataType.String:
                normalized = text;
                return true;
            case PublicationDataType.Binary:
                if (value is byte[] bytes)
                {
                    normalized = bytes;
                    return true;
                }
                try
                {
                    normalized = Convert.FromBase64String(text);
                    return true;
                }
                catch (FormatException)
                {
                    break;
                }
        }

        normalized = null;
        return false;
    }

    private static bool Equivalent(object? left, object? right) =>
        string.Equals(JsonSerializer.Serialize(left, JsonOptions), JsonSerializer.Serialize(right, JsonOptions), StringComparison.Ordinal);

    private static string Serialize(IReadOnlyDictionary<string, object?> values) =>
        JsonSerializer.Serialize(values, JsonOptions);

    private static PublicationSpreadsheetChangeMapResult Failure(string message) => new([], message);
}
