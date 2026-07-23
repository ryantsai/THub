using System.Net.Http.Headers;
using System.Text.Json;
using THub.Domain.Actions;

namespace THub.Application.Actions;

public enum WebhookAuthenticationKind
{
    None,
    Bearer,
    Basic,
}

public abstract record TrustedActionDefinition(TrustedActionKind Kind);

public sealed record WebhookActionDefinition(
    Uri Destination,
    string Method,
    string ContentType,
    WebhookAuthenticationKind Authentication,
    bool AllowPrivateAddresses,
    int TimeoutSeconds,
    int MaximumRequestBytes,
    int MaximumResponseBytes,
    IReadOnlyDictionary<string, string> Headers)
    : TrustedActionDefinition(TrustedActionKind.Webhook);

public sealed record ExecutableActionDefinition(
    string ExecutablePath,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    int TimeoutSeconds,
    int MaximumOutputCharacters,
    bool LoadUserProfile)
    : TrustedActionDefinition(TrustedActionKind.Executable);

public sealed class TrustedActionDefinitionException : Exception
{
    public TrustedActionDefinitionException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public TrustedActionDefinitionException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class TrustedActionDefinitionSerializer
{
    private static readonly HashSet<string> WebhookProperties =
    [
        "destination",
        "method",
        "contentType",
        "authentication",
        "allowPrivateAddresses",
        "timeoutSeconds",
        "maximumRequestBytes",
        "maximumResponseBytes",
        "headers",
    ];

    private static readonly HashSet<string> ExecutableProperties =
    [
        "executablePath",
        "workingDirectory",
        "arguments",
        "environment",
        "timeoutSeconds",
        "maximumOutputCharacters",
        "loadUserProfile",
    ];

    private static readonly HashSet<string> AllowedWebhookMethods =
        new(["POST", "PUT", "PATCH", "DELETE"], StringComparer.Ordinal);

    private static readonly HashSet<string> ForbiddenHeaders =
        new(
        [
            "Authorization",
            "Connection",
            "Content-Length",
            "Content-Type",
            "Host",
            "Proxy-Authorization",
            "Transfer-Encoding",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ForbiddenExecutables =
        new(
        [
            "cmd.exe",
            "powershell.exe",
            "pwsh.exe",
            "wscript.exe",
            "cscript.exe",
            "mshta.exe",
            "rundll32.exe",
            "regsvr32.exe",
        ],
        StringComparer.OrdinalIgnoreCase);

    public string Serialize(TrustedActionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition switch
        {
            WebhookActionDefinition webhook => JsonSerializer.Serialize(new
            {
                destination = webhook.Destination.AbsoluteUri,
                method = webhook.Method,
                contentType = webhook.ContentType,
                authentication = ToText(webhook.Authentication),
                allowPrivateAddresses = webhook.AllowPrivateAddresses,
                timeoutSeconds = webhook.TimeoutSeconds,
                maximumRequestBytes = webhook.MaximumRequestBytes,
                maximumResponseBytes = webhook.MaximumResponseBytes,
                headers = webhook.Headers,
            }),
            ExecutableActionDefinition executable => JsonSerializer.Serialize(new
            {
                executablePath = executable.ExecutablePath,
                workingDirectory = executable.WorkingDirectory,
                arguments = executable.Arguments,
                environment = executable.Environment,
                timeoutSeconds = executable.TimeoutSeconds,
                maximumOutputCharacters = executable.MaximumOutputCharacters,
                loadUserProfile = executable.LoadUserProfile,
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(definition)),
        };
    }

    public TrustedActionDefinition Deserialize(TrustedAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Deserialize(action.Kind, action.DefinitionJson);
    }

    public TrustedActionDefinition Deserialize(TrustedActionKind kind, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("trusted-action.definition.object", "The action definition must be a JSON object.");
            }

            return kind switch
            {
                TrustedActionKind.Webhook => ReadWebhook(root),
                TrustedActionKind.Executable => ReadExecutable(root),
                _ => throw Invalid("trusted-action.kind.unsupported", "The trusted action kind is unsupported."),
            };
        }
        catch (TrustedActionDefinitionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidOperationException
            or FormatException
            or OverflowException
            or ArgumentException)
        {
            throw new TrustedActionDefinitionException(
                "trusted-action.definition.invalid",
                "The trusted action definition is malformed or contains a value of the wrong type.",
                exception);
        }
    }

    private static WebhookActionDefinition ReadWebhook(JsonElement root)
    {
        EnsureOnly(root, WebhookProperties);
        var destinationText = ReadText(root, "destination", 2_048);
        if (!Uri.TryCreate(destinationText, UriKind.Absolute, out var destination) ||
            (!destination.IsDefaultPort && (destination.Port < 1 || destination.Port > 65_535)) ||
            !string.IsNullOrEmpty(destination.UserInfo) ||
            !string.IsNullOrEmpty(destination.Fragment) ||
            destination.Scheme is not ("https" or "http"))
        {
            throw Invalid(
                "trusted-action.webhook.destination",
                "Webhook destinations must be absolute HTTP(S) URLs without user information or fragments.");
        }

        var method = ReadText(root, "method", 16).ToUpperInvariant();
        if (!AllowedWebhookMethods.Contains(method))
        {
            throw Invalid(
                "trusted-action.webhook.method",
                "Webhook methods are limited to POST, PUT, PATCH, and DELETE.");
        }

        var contentType = ReadText(root, "contentType", 128);
        if (!MediaTypeHeaderValue.TryParse(contentType, out _))
        {
            throw Invalid("trusted-action.webhook.content-type", "The webhook content type is invalid.");
        }

        var authentication = ParseAuthentication(ReadText(root, "authentication", 16));
        if (authentication != WebhookAuthenticationKind.None &&
            !string.Equals(destination.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw Invalid(
                "trusted-action.webhook.authentication-tls",
                "Authenticated webhooks require HTTPS.");
        }

        return new(
            destination,
            method,
            contentType,
            authentication,
            ReadBoolean(root, "allowPrivateAddresses"),
            ReadInt(root, "timeoutSeconds", 1, 300),
            ReadInt(root, "maximumRequestBytes", 0, 1_048_576),
            ReadInt(root, "maximumResponseBytes", 0, 1_048_576),
            ReadDictionary(root, "headers", 32, 128, 2_048, ForbiddenHeaders));
    }

    private static ExecutableActionDefinition ReadExecutable(JsonElement root)
    {
        EnsureOnly(root, ExecutableProperties);
        var executablePath = ReadText(root, "executablePath", 1_024);
        var workingDirectory = ReadText(root, "workingDirectory", 1_024);
        if (!Path.IsPathFullyQualified(executablePath) ||
            !Path.IsPathFullyQualified(workingDirectory) ||
            IsDeviceOrUncPath(executablePath) ||
            IsDeviceOrUncPath(workingDirectory))
        {
            throw Invalid(
                "trusted-action.executable.path",
                "Executable and working-directory paths must be fully qualified local paths.");
        }

        if (ForbiddenExecutables.Contains(Path.GetFileName(executablePath)))
        {
            throw Invalid(
                "trusted-action.executable.shell",
                "Shells, script hosts, and indirect binary launchers cannot be trusted executables.");
        }

        return new(
            Path.GetFullPath(executablePath),
            Path.GetFullPath(workingDirectory),
            ReadStringArray(root, "arguments", 64, 2_048),
            ReadDictionary(root, "environment", 64, 128, 4_096, forbiddenKeys: null),
            ReadInt(root, "timeoutSeconds", 1, 86_400),
            ReadInt(root, "maximumOutputCharacters", 0, 1_000_000),
            ReadBoolean(root, "loadUserProfile"));
    }

    private static bool IsDeviceOrUncPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ||
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
        path.StartsWith(@"\\.\", StringComparison.Ordinal);

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement root,
        string propertyName,
        int maximumItems,
        int maximumLength)
    {
        var element = Require(root, propertyName);
        if (element.ValueKind != JsonValueKind.Array ||
            element.GetArrayLength() > maximumItems)
        {
            throw Invalid(
                "trusted-action.definition.array",
                $"'{propertyName}' must be an array containing at most {maximumItems} values.");
        }

        return element.EnumerateArray()
            .Select(item =>
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw Invalid(
                        "trusted-action.definition.array-value",
                        $"Every '{propertyName}' value must be text.");
                }

                var value = item.GetString() ?? string.Empty;
                if (value.Length > maximumLength ||
                    value.Any(character => char.IsControl(character) && character is not '\t'))
                {
                    throw Invalid(
                        "trusted-action.definition.array-value",
                        $"A '{propertyName}' value is invalid or exceeds {maximumLength} characters.");
                }

                ValidateArgumentTemplate(value);
                return value;
            })
            .ToArray();
    }

    private static void ValidateArgumentTemplate(string value)
    {
        var remainder = value
            .Replace("{runId}", string.Empty, StringComparison.Ordinal)
            .Replace("{nodeId}", string.Empty, StringComparison.Ordinal)
            .Replace("{attempt}", string.Empty, StringComparison.Ordinal)
            .Replace("{inputRowCount}", string.Empty, StringComparison.Ordinal);
        if (remainder.Contains('{', StringComparison.Ordinal) ||
            remainder.Contains('}', StringComparison.Ordinal))
        {
            throw Invalid(
                "trusted-action.executable.argument-template",
                "Executable arguments may use only {runId}, {nodeId}, {attempt}, and {inputRowCount} placeholders.");
        }
    }

    private static IReadOnlyDictionary<string, string> ReadDictionary(
        JsonElement root,
        string propertyName,
        int maximumItems,
        int maximumKeyLength,
        int maximumValueLength,
        IReadOnlySet<string>? forbiddenKeys)
    {
        var element = Require(root, propertyName);
        if (element.ValueKind != JsonValueKind.Object ||
            element.EnumerateObject().Take(maximumItems + 1).Count() > maximumItems)
        {
            throw Invalid(
                "trusted-action.definition.dictionary",
                $"'{propertyName}' must contain at most {maximumItems} entries.");
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Length is < 1 ||
                property.Name.Length > maximumKeyLength ||
                property.Name.Any(character =>
                    !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')) ||
                forbiddenKeys?.Contains(property.Name) == true ||
                property.Value.ValueKind != JsonValueKind.String)
            {
                throw Invalid(
                    "trusted-action.definition.dictionary-key",
                    $"A '{propertyName}' entry name or value is unsupported.");
            }

            var value = property.Value.GetString() ?? string.Empty;
            if (value.Length > maximumValueLength ||
                value.Any(character => char.IsControl(character) && character is not '\t'))
            {
                throw Invalid(
                    "trusted-action.definition.dictionary-value",
                    $"A '{propertyName}' value is invalid or exceeds {maximumValueLength} characters.");
            }

            if (!result.TryAdd(property.Name, value))
            {
                throw Invalid(
                    "trusted-action.definition.dictionary-duplicate",
                    $"'{propertyName}' contains a duplicate entry.");
            }
        }

        return result;
    }

    private static WebhookAuthenticationKind ParseAuthentication(string value) =>
        value.ToLowerInvariant() switch
        {
            "none" => WebhookAuthenticationKind.None,
            "bearer" => WebhookAuthenticationKind.Bearer,
            "basic" => WebhookAuthenticationKind.Basic,
            _ => throw Invalid(
                "trusted-action.webhook.authentication",
                "Webhook authentication must be none, bearer, or basic."),
        };

    private static string ToText(WebhookAuthenticationKind value) => value switch
    {
        WebhookAuthenticationKind.None => "none",
        WebhookAuthenticationKind.Bearer => "bearer",
        WebhookAuthenticationKind.Basic => "basic",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static int ReadInt(JsonElement root, string propertyName, int minimum, int maximum)
    {
        var element = Require(root, propertyName);
        if (!element.TryGetInt32(out var value) || value < minimum || value > maximum)
        {
            throw Invalid(
                "trusted-action.definition.number",
                $"'{propertyName}' must be between {minimum} and {maximum}.");
        }

        return value;
    }

    private static bool ReadBoolean(JsonElement root, string propertyName)
    {
        var element = Require(root, propertyName);
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Invalid(
                "trusted-action.definition.boolean",
                $"'{propertyName}' must be true or false.");
        }

        return element.GetBoolean();
    }

    private static string ReadText(JsonElement root, string propertyName, int maximumLength)
    {
        var element = Require(root, propertyName);
        if (element.ValueKind != JsonValueKind.String)
        {
            throw Invalid(
                "trusted-action.definition.text",
                $"'{propertyName}' must be text.");
        }

        var value = element.GetString()?.Trim() ?? string.Empty;
        if (value.Length is < 1 || value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw Invalid(
                "trusted-action.definition.text",
                $"'{propertyName}' is required and cannot exceed {maximumLength} characters.");
        }

        return value;
    }

    private static JsonElement Require(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value)
            ? value
            : throw Invalid(
                "trusted-action.definition.required",
                $"Required property '{propertyName}' is missing.");

    private static void EnsureOnly(JsonElement root, IReadOnlySet<string> allowed)
    {
        var encountered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!encountered.Add(property.Name))
            {
                throw Invalid(
                    "trusted-action.definition.duplicate",
                    $"Property '{property.Name}' is duplicated.");
            }

            if (!allowed.Contains(property.Name))
            {
                throw Invalid(
                    "trusted-action.definition.unsupported",
                    $"Property '{property.Name}' is unsupported.");
            }
        }
    }

    private static TrustedActionDefinitionException Invalid(string code, string message) =>
        new(code, message);
}
