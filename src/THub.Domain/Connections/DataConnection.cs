using System.Text.Json;

namespace THub.Domain.Connections;

public enum ConnectionKind
{
    SqlServer,
    CsvFile,
    ExcelFile
}

public sealed class DataConnection
{
    public const int MaximumNameLength = 200;
    public const int MaximumIdentityLength = 256;
    public const int MaximumConfigurationLength = 64 * 1_024;

    private static readonly HashSet<string> ForbiddenSecretProperties = new(
        [
            "password",
            "pwd",
            "secret",
            "clientSecret",
            "accessToken",
            "bearerToken",
            "apiKey",
            "connectionString"
        ],
        StringComparer.OrdinalIgnoreCase);

    private DataConnection() { }

    public DataConnection(
        string name,
        ConnectionKind kind,
        string configurationJson,
        string createdBy)
        : this(name, kind, configurationJson, createdBy, DateTimeOffset.UtcNow)
    {
    }

    public DataConnection(
        string name,
        ConnectionKind kind,
        string configurationJson,
        string createdBy,
        DateTimeOffset createdAtUtc)
    {
        Id = Guid.NewGuid();
        Name = Require(name, nameof(name), MaximumNameLength);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        ConfigurationJson = ValidateConfiguration(configurationJson);
        CreatedBy = Require(createdBy, nameof(createdBy), MaximumIdentityLength);
        CreatedAtUtc = RequireTimestamp(createdAtUtc, nameof(createdAtUtc));
        UpdatedAtUtc = CreatedAtUtc;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public ConnectionKind Kind { get; private set; }

    public string ConfigurationJson { get; private set; } = "{}";

    public bool IsEnabled { get; private set; } = true;

    public string CreatedBy { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void Rename(string name, DateTimeOffset changedAtUtc)
    {
        Name = Require(name, nameof(name), MaximumNameLength);
        Touch(changedAtUtc);
    }

    public void UpdateConfiguration(string configurationJson, DateTimeOffset changedAtUtc)
    {
        ConfigurationJson = ValidateConfiguration(configurationJson);
        Touch(changedAtUtc);
    }

    public void Enable(DateTimeOffset changedAtUtc) => SetEnabled(true, changedAtUtc);

    public void Disable(DateTimeOffset changedAtUtc) => SetEnabled(false, changedAtUtc);

    private void SetEnabled(bool isEnabled, DateTimeOffset changedAtUtc)
    {
        IsEnabled = isEnabled;
        Touch(changedAtUtc);
    }

    private void Touch(DateTimeOffset changedAtUtc)
    {
        var timestamp = RequireTimestamp(changedAtUtc, nameof(changedAtUtc));
        if (timestamp < UpdatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(changedAtUtc),
                "Connection timestamps cannot move backwards.");
        }

        UpdatedAtUtc = timestamp;
    }

    private static string ValidateConfiguration(string configurationJson)
    {
        var value = Require(
            configurationJson,
            nameof(configurationJson),
            MaximumConfigurationLength);

        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    "Connection configuration must be a JSON object.",
                    nameof(configurationJson));
            }

            RejectInlineSecrets(document.RootElement, nameof(configurationJson));
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "Connection configuration must contain valid JSON.",
                nameof(configurationJson),
                exception);
        }

        return value;
    }

    private static void RejectInlineSecrets(JsonElement element, string parameterName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (ForbiddenSecretProperties.Contains(property.Name))
                {
                    throw new ArgumentException(
                        $"Connection configuration cannot store inline secret property '{property.Name}'.",
                        parameterName);
                }

                RejectInlineSecrets(property.Value, parameterName);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectInlineSecrets(item, parameterName);
            }
        }
    }

    private static string Require(string value, string parameterName, int maximumLength)
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

    private static DateTimeOffset RequireTimestamp(DateTimeOffset value, string parameterName)
    {
        if (value == default)
        {
            throw new ArgumentOutOfRangeException(parameterName, "A timestamp is required.");
        }

        return value.ToUniversalTime();
    }
}
