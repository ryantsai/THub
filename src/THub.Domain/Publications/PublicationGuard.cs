using System.Text.Json;

namespace THub.Domain.Publications;

internal static class PublicationGuard
{
    public static Guid RequireId(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An empty identifier is not valid.", parameterName);
        }

        return value;
    }

    public static string Require(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Value cannot exceed {maximumLength} characters.");
        }

        return normalized;
    }

    public static string? Optional(string? value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Require(value, parameterName, maximumLength);
    }

    public static DateTimeOffset AsUtc(DateTimeOffset value) => value.ToUniversalTime();

    public static DateTimeOffset NotBefore(
        DateTimeOffset value,
        DateTimeOffset minimum,
        string parameterName)
    {
        var utc = AsUtc(value);
        if (utc < minimum)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Timestamp cannot move backwards.");
        }

        return utc;
    }

    public static TEnum RequireDefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Value is not defined.");
        }

        return value;
    }

    public static string RequireJsonObject(string value, string parameterName, int maximumLength)
    {
        var json = Require(value, parameterName, maximumLength);

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("JSON value must be an object.", parameterName);
            }
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Value must contain valid JSON.", parameterName, exception);
        }

        return json;
    }
}
