namespace THub.Domain.Publications;

public sealed class PublicationVersionSettings
{
    public const int MaximumAllowedPageSize = 1_000;
    public const int MaximumAllowedEditorWindowSize = 1_000;
    public const int MaximumAllowedRequestsPerWindow = 100_000;
    public const int MaximumAllowedRateLimitWindowSeconds = 3_600;
    public const int MaximumAllowedConcurrentRequests = 100;
    public const int MaximumAllowedTimeoutSeconds = 300;
    public const int MinimumAllowedResponseBytes = 1_024;
    public const int MaximumAllowedResponseBytes = 100 * 1_024 * 1_024;

    private PublicationVersionSettings()
    {
    }

    public PublicationVersionSettings(
        int defaultPageSize = 100,
        int maximumPageSize = 1_000,
        int requestsPerWindow = 600,
        int rateLimitWindowSeconds = 60,
        int maximumConcurrentRequests = 10,
        int editorWindowSize = 250,
        int requestTimeoutSeconds = 30,
        int commandTimeoutSeconds = 30,
        int maximumResponseBytes = 10 * 1_024 * 1_024)
    {
        DefaultPageSize = RequireRange(
            defaultPageSize,
            1,
            MaximumAllowedPageSize,
            nameof(defaultPageSize));
        MaximumPageSize = RequireRange(
            maximumPageSize,
            1,
            MaximumAllowedPageSize,
            nameof(maximumPageSize));
        if (DefaultPageSize > MaximumPageSize)
        {
            throw new ArgumentException("Default page size cannot exceed maximum page size.", nameof(defaultPageSize));
        }

        RequestsPerWindow = RequireRange(
            requestsPerWindow,
            1,
            MaximumAllowedRequestsPerWindow,
            nameof(requestsPerWindow));
        RateLimitWindowSeconds = RequireRange(
            rateLimitWindowSeconds,
            1,
            MaximumAllowedRateLimitWindowSeconds,
            nameof(rateLimitWindowSeconds));
        MaximumConcurrentRequests = RequireRange(
            maximumConcurrentRequests,
            1,
            MaximumAllowedConcurrentRequests,
            nameof(maximumConcurrentRequests));
        EditorWindowSize = RequireRange(
            editorWindowSize,
            1,
            MaximumAllowedEditorWindowSize,
            nameof(editorWindowSize));
        RequestTimeoutSeconds = RequireRange(
            requestTimeoutSeconds,
            1,
            MaximumAllowedTimeoutSeconds,
            nameof(requestTimeoutSeconds));
        CommandTimeoutSeconds = RequireRange(
            commandTimeoutSeconds,
            1,
            MaximumAllowedTimeoutSeconds,
            nameof(commandTimeoutSeconds));
        MaximumResponseBytes = RequireRange(
            maximumResponseBytes,
            MinimumAllowedResponseBytes,
            MaximumAllowedResponseBytes,
            nameof(maximumResponseBytes));
    }

    public int DefaultPageSize { get; private set; }

    public int MaximumPageSize { get; private set; }

    public int RequestsPerWindow { get; private set; }

    public int RateLimitWindowSeconds { get; private set; }

    public int MaximumConcurrentRequests { get; private set; }

    public int EditorWindowSize { get; private set; }

    public int RequestTimeoutSeconds { get; private set; }

    public int CommandTimeoutSeconds { get; private set; }

    public int MaximumResponseBytes { get; private set; }

    private static int RequireRange(int value, int minimum, int maximum, string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Value must be between {minimum} and {maximum}.");
        }

        return value;
    }
}
