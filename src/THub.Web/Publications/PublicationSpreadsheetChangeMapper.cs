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

public enum PublicationCellValidationCode
{
    Required,
    InvalidType,
    MaximumLength,
    MaximumBinaryLength,
    NumericPrecision,
    NumericScale,
}

public sealed record PublicationCellValidationFailure(
    PublicationCellValidationCode Code,
    int? Constraint = null);

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
            if (!TryNormalizeCellValue(value, column, out var typed, out var failure))
            {
                error = FormatFailure(rowIndex, column, failure!);
                return null;
            }

            normalized.Add(column.PublicAlias, typed);
        }

        error = null;
        return normalized;
    }

    public static bool TryNormalizeCellValue(
        object? value,
        PublicationColumnDto column,
        out object? normalized,
        out PublicationCellValidationFailure? failure)
    {
        if (value is null || value is DBNull || value is string { Length: 0 } && column.DataType != PublicationDataType.String)
        {
            normalized = null;
            failure = column.IsNullable
                ? null
                : new PublicationCellValidationFailure(PublicationCellValidationCode.Required);
            return failure is null;
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        switch (column.DataType)
        {
            case PublicationDataType.Boolean:
                if (value is bool boolean || bool.TryParse(text, out boolean))
                {
                    normalized = boolean;
                    failure = null;
                    return true;
                }
                break;
            case PublicationDataType.Byte:
                if (value is byte byteValue || byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out byteValue))
                {
                    normalized = byteValue;
                    failure = null;
                    return true;
                }
                break;
            case PublicationDataType.Int16:
                if (value is short shortValue || short.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out shortValue))
                {
                    normalized = shortValue;
                    failure = null;
                    return true;
                }
                break;
            case PublicationDataType.Int32:
                if (value is int intValue || int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
                {
                    normalized = intValue;
                    failure = null;
                    return true;
                }
                break;
            case PublicationDataType.Int64:
                if (value is long longValue || long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out longValue))
                {
                    normalized = longValue;
                    failure = null;
                    return true;
                }
                break;
            case PublicationDataType.Decimal:
                if (value is decimal decimalValue || decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimalValue))
                {
                    if (!FitsDecimalConstraints(decimalValue, column, out failure))
                    {
                        normalized = null;
                        return false;
                    }
                    normalized = decimalValue;
                    return true;
                }
                break;
            case PublicationDataType.Single:
                if ((value is float singleValue || float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out singleValue)) && float.IsFinite(singleValue))
                {
                    normalized = singleValue;
                    failure = null;
                    return true;
                }
                break;
            case PublicationDataType.Double:
                if ((value is double doubleValue || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out doubleValue)) && double.IsFinite(doubleValue))
                {
                    normalized = doubleValue;
                    failure = null;
                    return true;
                }
                break;
            case PublicationDataType.Date:
                if (value is DateOnly dateOnly)
                {
                    normalized = dateOnly;
                    failure = null;
                    return true;
                }
                if (value is DateTime dateTime)
                {
                    normalized = DateOnly.FromDateTime(dateTime);
                    failure = null;
                    return true;
                }
                if (TryParseDate(text, out dateOnly))
                {
                    normalized = dateOnly;
                    failure = null;
                    return true;
                }
                break;
            case PublicationDataType.DateTime:
                if (value is DateTime timestamp)
                {
                    normalized = timestamp;
                    failure = null;
                    return true;
                }
                if (TryParseDateTime(text, out timestamp))
                {
                    normalized = timestamp;
                    failure = null;
                    return true;
                }
                break;
            case PublicationDataType.DateTimeOffset:
                if (value is DateTimeOffset offset)
                {
                    normalized = offset;
                    failure = null;
                    return true;
                }
                if (TryParseDateTimeOffset(text, out offset))
                {
                    normalized = offset;
                    failure = null;
                    return true;
                }
                break;
            case PublicationDataType.Time:
                if (value is TimeOnly timeOnly || TryParseTime(text, out timeOnly))
                {
                    normalized = timeOnly;
                    failure = null;
                    return true;
                }
                break;
            case PublicationDataType.Guid:
                if (value is Guid guid || Guid.TryParse(text, out guid))
                {
                    normalized = guid;
                    failure = null;
                    return true;
                }
                break;
            case PublicationDataType.String:
                if (column.MaximumLength is int maximumLength && text.Length > maximumLength)
                {
                    normalized = null;
                    failure = new PublicationCellValidationFailure(
                        PublicationCellValidationCode.MaximumLength,
                        maximumLength);
                    return false;
                }
                normalized = text;
                failure = null;
                return true;
            case PublicationDataType.Binary:
                if (value is byte[] bytes)
                {
                    if (column.MaximumLength is int maximumBinaryLength && bytes.Length > maximumBinaryLength)
                    {
                        normalized = null;
                        failure = new PublicationCellValidationFailure(
                            PublicationCellValidationCode.MaximumBinaryLength,
                            maximumBinaryLength);
                        return false;
                    }
                    normalized = bytes;
                    failure = null;
                    return true;
                }
                try
                {
                    var parsed = Convert.FromBase64String(text);
                    if (column.MaximumLength is int maximumBinaryLength && parsed.Length > maximumBinaryLength)
                    {
                        normalized = null;
                        failure = new PublicationCellValidationFailure(
                            PublicationCellValidationCode.MaximumBinaryLength,
                            maximumBinaryLength);
                        return false;
                    }
                    normalized = parsed;
                    failure = null;
                    return true;
                }
                catch (FormatException)
                {
                    break;
                }
        }

        normalized = null;
        failure = new PublicationCellValidationFailure(PublicationCellValidationCode.InvalidType);
        return false;
    }

    private static bool FitsDecimalConstraints(
        decimal value,
        PublicationColumnDto column,
        out PublicationCellValidationFailure? failure)
    {
        if (column.NumericScale is byte scale &&
            decimal.Round(value, scale, MidpointRounding.ToEven) != value)
        {
            failure = new PublicationCellValidationFailure(
                PublicationCellValidationCode.NumericScale,
                scale);
            return false;
        }

        if (column.NumericPrecision is byte precision)
        {
            var allowedScale = column.NumericScale ?? 0;
            var allowedIntegralDigits = precision - allowedScale;
            var integral = decimal.Truncate(value);
            var integralDigits = integral == 0
                ? 0
                : integral.ToString("0", CultureInfo.InvariantCulture).TrimStart('-').Length;
            if (integralDigits > allowedIntegralDigits)
            {
                failure = new PublicationCellValidationFailure(
                    PublicationCellValidationCode.NumericPrecision,
                    precision);
                return false;
            }
        }

        failure = null;
        return true;
    }

    private static bool TryParseDate(string text, out DateOnly value) =>
        DateOnly.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out value) ||
        DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out value);

    private static bool TryParseDateTime(string text, out DateTime value) =>
        DateTime.TryParse(
            text,
            CultureInfo.CurrentCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
            out value) ||
        DateTime.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
            out value);

    private static bool TryParseDateTimeOffset(string text, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(
            text,
            CultureInfo.CurrentCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
            out value) ||
        DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
            out value);

    private static bool TryParseTime(string text, out TimeOnly value) =>
        TimeOnly.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out value) ||
        TimeOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out value);

    private static string FormatFailure(
        int rowIndex,
        PublicationColumnDto column,
        PublicationCellValidationFailure failure) =>
        failure.Code switch
        {
            PublicationCellValidationCode.Required =>
                $"Row {rowIndex + 1}, column '{column.PublicAlias}' is required.",
            PublicationCellValidationCode.MaximumLength =>
                $"Row {rowIndex + 1}, column '{column.PublicAlias}' cannot exceed {failure.Constraint} characters.",
            PublicationCellValidationCode.MaximumBinaryLength =>
                $"Row {rowIndex + 1}, column '{column.PublicAlias}' cannot exceed {failure.Constraint} bytes.",
            PublicationCellValidationCode.NumericPrecision =>
                $"Row {rowIndex + 1}, column '{column.PublicAlias}' exceeds numeric precision {failure.Constraint}.",
            PublicationCellValidationCode.NumericScale =>
                $"Row {rowIndex + 1}, column '{column.PublicAlias}' cannot have more than {failure.Constraint} decimal places.",
            _ => $"Row {rowIndex + 1}, column '{column.PublicAlias}' is not a valid {column.DataType} value.",
        };

    private static bool Equivalent(object? left, object? right) =>
        left is byte[] leftBytes && right is byte[] rightBytes
            ? leftBytes.AsSpan().SequenceEqual(rightBytes)
            : Equals(left, right);

    private static string Serialize(IReadOnlyDictionary<string, object?> values) =>
        JsonSerializer.Serialize(values, JsonOptions);

    private static PublicationSpreadsheetChangeMapResult Failure(string message) => new([], message);
}
