namespace THub.Domain.Actions;

public enum TrustedActionKind
{
    Webhook,
    Executable,
}

public sealed class TrustedAction
{
    public const int MaximumNameLength = 100;
    public const int MaximumDefinitionCharacters = 32_000;
    public const int MaximumIdentityLength = 256;
    public const int MaximumCredentialReferenceLength = 185;

    private TrustedAction()
    {
    }

    public TrustedAction(
        Guid id,
        string name,
        TrustedActionKind kind,
        string definitionJson,
        string? credentialReference,
        string createdBy,
        DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A trusted action identifier is required.", nameof(id));
        }

        Id = id;
        Name = RequireText(name, MaximumNameLength, nameof(name));
        Kind = Enum.IsDefined(kind) ? kind : throw new ArgumentOutOfRangeException(nameof(kind));
        DefinitionJson = RequireText(
            definitionJson,
            MaximumDefinitionCharacters,
            nameof(definitionJson));
        CredentialReference = NormalizeCredentialReference(credentialReference);
        CreatedBy = RequireText(createdBy, MaximumIdentityLength, nameof(createdBy));
        UpdatedBy = CreatedBy;
        CreatedAtUtc = RequireUtc(createdAtUtc, nameof(createdAtUtc));
        UpdatedAtUtc = CreatedAtUtc;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public TrustedActionKind Kind { get; private set; }
    public string DefinitionJson { get; private set; } = "{}";
    public string? CredentialReference { get; private set; }
    public bool IsEnabled { get; private set; } = true;
    public string CreatedBy { get; private set; } = string.Empty;
    public string UpdatedBy { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void Update(
        string name,
        string definitionJson,
        string? credentialReference,
        string changedBy,
        DateTimeOffset changedAtUtc)
    {
        Name = RequireText(name, MaximumNameLength, nameof(name));
        DefinitionJson = RequireText(
            definitionJson,
            MaximumDefinitionCharacters,
            nameof(definitionJson));
        CredentialReference = NormalizeCredentialReference(credentialReference);
        UpdatedBy = RequireText(changedBy, MaximumIdentityLength, nameof(changedBy));
        UpdatedAtUtc = RequireLater(changedAtUtc);
    }

    public void SetEnabled(bool enabled, string changedBy, DateTimeOffset changedAtUtc)
    {
        IsEnabled = enabled;
        UpdatedBy = RequireText(changedBy, MaximumIdentityLength, nameof(changedBy));
        UpdatedAtUtc = RequireLater(changedAtUtc);
    }

    private DateTimeOffset RequireLater(DateTimeOffset value)
    {
        var normalized = RequireUtc(value, nameof(value));
        if (normalized < UpdatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return normalized;
    }

    private static string? NormalizeCredentialReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > MaximumCredentialReferenceLength ||
            normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new ArgumentException(
                "Credential references may contain only letters, numbers, dots, hyphens, and underscores.",
                nameof(value));
        }

        return normalized;
    }

    private static string RequireText(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return normalized;
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName) =>
        value == default
            ? throw new ArgumentOutOfRangeException(parameterName)
            : value.ToUniversalTime();
}
