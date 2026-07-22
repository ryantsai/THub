namespace THub.Domain;

internal static class DomainGuard
{
    public static string Require(string value, string parameterName, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Value cannot exceed {maxLength} characters.");
        }

        return normalized;
    }

    public static string Optional(string? value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return Require(value, parameterName, maxLength);
    }

    public static Guid RequireId(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A non-empty identifier is required.", parameterName);
        }

        return value;
    }

    public static int RequirePositive(int value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Value must be positive.");
        }

        return value;
    }

    public static DateTimeOffset Utc(DateTimeOffset value, string parameterName)
    {
        if (value == default)
        {
            throw new ArgumentOutOfRangeException(parameterName, "A timestamp is required.");
        }

        return value.ToUniversalTime();
    }

    public static DateTimeOffset OnOrAfter(
        DateTimeOffset value,
        DateTimeOffset lowerBound,
        string parameterName)
    {
        var utc = Utc(value, parameterName);
        if (utc < lowerBound)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Timestamp cannot be earlier than {lowerBound:O}.");
        }

        return utc;
    }

    public static TimeSpan LeaseDuration(
        TimeSpan value,
        string parameterName,
        TimeSpan maximum)
    {
        if (value <= TimeSpan.Zero || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Duration must be greater than zero and no greater than {maximum}.");
        }

        return value;
    }
}
