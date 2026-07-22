using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using THub.Domain.Publications;

namespace THub.Infrastructure.Publications;

internal static class SqlPublicationValueConverter
{
    public static bool TryParse(
        string value,
        PublicationColumn column,
        out object? parsed)
    {
        ArgumentNullException.ThrowIfNull(column);
        parsed = null;
        var style = NumberStyles.Integer;
        var culture = CultureInfo.InvariantCulture;
        switch (column.DataType)
        {
            case PublicationDataType.Boolean when bool.TryParse(value, out var boolean):
                parsed = boolean;
                return true;
            case PublicationDataType.Byte when byte.TryParse(value, style, culture, out var byteValue):
                parsed = byteValue;
                return true;
            case PublicationDataType.Int16 when short.TryParse(value, style, culture, out var int16):
                parsed = int16;
                return true;
            case PublicationDataType.Int32 when int.TryParse(value, style, culture, out var int32):
                parsed = int32;
                return true;
            case PublicationDataType.Int64 when long.TryParse(value, style, culture, out var int64):
                parsed = int64;
                return true;
            case PublicationDataType.Decimal when decimal.TryParse(
                value,
                NumberStyles.Number,
                culture,
                out var decimalValue):
                parsed = decimalValue;
                return true;
            case PublicationDataType.Single when float.TryParse(
                value,
                NumberStyles.Float,
                culture,
                out var single) && float.IsFinite(single):
                parsed = single;
                return true;
            case PublicationDataType.Double when double.TryParse(
                value,
                NumberStyles.Float,
                culture,
                out var doubleValue) && double.IsFinite(doubleValue):
                parsed = doubleValue;
                return true;
            case PublicationDataType.Date when DateOnly.TryParse(
                value,
                culture,
                DateTimeStyles.None,
                out var date):
                parsed = date.ToDateTime(TimeOnly.MinValue);
                return true;
            case PublicationDataType.DateTime when DateTime.TryParse(
                value,
                culture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
                out var dateTime):
                parsed = dateTime;
                return true;
            case PublicationDataType.DateTimeOffset when DateTimeOffset.TryParse(
                value,
                culture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
                out var dateTimeOffset):
                parsed = dateTimeOffset;
                return true;
            case PublicationDataType.Time when TimeSpan.TryParse(value, culture, out var time):
                parsed = time;
                return true;
            case PublicationDataType.Guid when Guid.TryParse(value, out var guid):
                parsed = guid;
                return true;
            case PublicationDataType.String when column.MaximumLength is null || value.Length <= column.MaximumLength:
                parsed = value;
                return true;
            case PublicationDataType.Binary:
                try
                {
                    var binary = Convert.FromBase64String(value);
                    if (column.MaximumLength is not null && binary.Length > column.MaximumLength)
                    {
                        return false;
                    }

                    parsed = binary;
                    return true;
                }
                catch (FormatException)
                {
                    return false;
                }
            default:
                return false;
        }
    }

    public static string FormatCursorValue(object value, PublicationDataType dataType) => dataType switch
    {
        PublicationDataType.Boolean => Convert.ToBoolean(value, CultureInfo.InvariantCulture)
            ? "true"
            : "false",
        PublicationDataType.Byte or PublicationDataType.Int16 or PublicationDataType.Int32 or
            PublicationDataType.Int64 or PublicationDataType.Decimal or PublicationDataType.Single or
            PublicationDataType.Double => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        PublicationDataType.Date => value switch
        {
            DateOnly date => date.ToString("O", CultureInfo.InvariantCulture),
            DateTime dateTime => DateOnly.FromDateTime(dateTime).ToString("O", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        },
        PublicationDataType.DateTime => Convert.ToDateTime(value, CultureInfo.InvariantCulture)
            .ToString("O", CultureInfo.InvariantCulture),
        PublicationDataType.DateTimeOffset => value switch
        {
            DateTimeOffset offset => offset.ToString("O", CultureInfo.InvariantCulture),
            DateTime dateTime => new DateTimeOffset(dateTime).ToString("O", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        },
        PublicationDataType.Time => value switch
        {
            TimeOnly timeOnly => timeOnly.ToString("O", CultureInfo.InvariantCulture),
            TimeSpan timeSpan => timeSpan.ToString("c", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        },
        PublicationDataType.Guid => value is Guid guid
            ? guid.ToString("D", CultureInfo.InvariantCulture)
            : Convert.ToString(value, CultureInfo.InvariantCulture)!,
        PublicationDataType.Binary => Convert.ToBase64String((byte[])value),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
    };

    public static SqlParameter CreateParameter(
        string name,
        object? value,
        PublicationColumn column)
    {
        var parameter = new SqlParameter(name, MapDbType(column))
        {
            Value = value ?? DBNull.Value,
            IsNullable = column.IsNullable,
        };
        if (column.DataType == PublicationDataType.String)
        {
            parameter.Size = column.MaximumLength is null or > 4_000
                ? -1
                : column.MaximumLength.Value;
        }
        else if (column.DataType == PublicationDataType.Binary)
        {
            parameter.Size = column.MaximumLength ?? -1;
        }
        else if (column.DataType == PublicationDataType.Decimal)
        {
            parameter.Precision = column.NumericPrecision ?? 38;
            parameter.Scale = column.NumericScale ?? 18;
        }

        return parameter;
    }

    private static SqlDbType MapDbType(PublicationColumn column) => column.DataType switch
    {
        PublicationDataType.Boolean => SqlDbType.Bit,
        PublicationDataType.Byte => SqlDbType.TinyInt,
        PublicationDataType.Int16 => SqlDbType.SmallInt,
        PublicationDataType.Int32 => SqlDbType.Int,
        PublicationDataType.Int64 => SqlDbType.BigInt,
        PublicationDataType.Decimal => SqlDbType.Decimal,
        PublicationDataType.Single => SqlDbType.Real,
        PublicationDataType.Double => SqlDbType.Float,
        PublicationDataType.Date => SqlDbType.Date,
        PublicationDataType.DateTime => SourceTypeStartsWith(column, "datetime2")
            ? SqlDbType.DateTime2
            : SqlDbType.DateTime,
        PublicationDataType.DateTimeOffset => SqlDbType.DateTimeOffset,
        PublicationDataType.Time => SqlDbType.Time,
        PublicationDataType.Guid => SqlDbType.UniqueIdentifier,
        PublicationDataType.String => SourceTypeStartsWith(column, "varchar") ||
            SourceTypeStartsWith(column, "char")
                ? SqlDbType.VarChar
                : SqlDbType.NVarChar,
        PublicationDataType.Binary => SourceTypeStartsWith(column, "varbinary")
            ? SqlDbType.VarBinary
            : SqlDbType.Binary,
        _ => throw new ArgumentOutOfRangeException(nameof(column), column.DataType, "Unsupported data type."),
    };

    private static bool SourceTypeStartsWith(PublicationColumn column, string prefix) =>
        column.SourceTypeName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
