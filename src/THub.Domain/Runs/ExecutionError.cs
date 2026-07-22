namespace THub.Domain.Runs;

public enum ExecutionErrorCategory
{
    Validation,
    Configuration,
    Authentication,
    Authorization,
    Connectivity,
    Timeout,
    RateLimited,
    Data,
    ResourceLimit,
    ExternalSideEffect,
    Cancelled,
    Unknown
}

/// <summary>
/// A deliberately bounded and normalized failure safe to persist with run metadata.
/// Raw exceptions and connector payloads do not belong in this value.
/// </summary>
public sealed record ExecutionError
{
    public const int MaximumCodeLength = 64;
    public const int MaximumSummaryLength = 1_024;

    public ExecutionError(
        string code,
        ExecutionErrorCategory category,
        string summary,
        bool isRetryable)
    {
        Code = ValidateCode(code);
        Category = category;
        Summary = NormalizeSummary(summary);
        IsRetryable = isRetryable;
    }

    public string Code { get; }

    public ExecutionErrorCategory Category { get; }

    public string Summary { get; }

    public bool IsRetryable { get; }

    private static string ValidateCode(string code)
    {
        var normalized = DomainGuard.Require(code, nameof(code), MaximumCodeLength);
        if (normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character)
                    || character is '.' or '-' or '_')))
        {
            throw new ArgumentException(
                "Error codes may contain only ASCII letters, digits, periods, hyphens, and underscores.",
                nameof(code));
        }

        return normalized;
    }

    private static string NormalizeSummary(string summary)
    {
        var normalized = DomainGuard.Require(
            summary,
            nameof(summary),
            MaximumSummaryLength);

        if (normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Error summaries cannot contain control characters.",
                nameof(summary));
        }

        return normalized;
    }
}
