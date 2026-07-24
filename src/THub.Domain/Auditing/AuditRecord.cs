namespace THub.Domain.Auditing;

public enum AuditActorKind
{
    User,
    System,
    ApiToken,
}

public enum AuditOutcome
{
    Succeeded,
    Failed,
    Denied,
}

public sealed class AuditRecord
{
    public const int MaximumActorIdentifierLength = 256;
    public const int MaximumSourceLength = 100;
    public const int MaximumActionLength = 100;
    public const int MaximumResourceTypeLength = 100;
    public const int MaximumResourceIdentifierLength = 200;
    public const int MaximumCorrelationIdentifierLength = 200;

    private AuditRecord()
    {
    }

    public AuditRecord(
        Guid id,
        DateTimeOffset occurredAtUtc,
        AuditActorKind actorKind,
        string actorIdentifier,
        string source,
        string action,
        AuditOutcome outcome,
        string resourceType,
        string? resourceIdentifier = null,
        string? correlationIdentifier = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An audit identifier is required.", nameof(id));
        }

        if (occurredAtUtc == default)
        {
            throw new ArgumentOutOfRangeException(nameof(occurredAtUtc));
        }

        if (!Enum.IsDefined(actorKind))
        {
            throw new ArgumentOutOfRangeException(nameof(actorKind));
        }

        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        Id = id;
        OccurredAtUtc = occurredAtUtc.ToUniversalTime();
        ActorKind = actorKind;
        ActorIdentifier = Require(
            actorIdentifier,
            MaximumActorIdentifierLength,
            nameof(actorIdentifier));
        Source = RequireMachineName(source, MaximumSourceLength, nameof(source));
        Action = RequireMachineName(action, MaximumActionLength, nameof(action));
        Outcome = outcome;
        ResourceType = RequireMachineName(
            resourceType,
            MaximumResourceTypeLength,
            nameof(resourceType));
        ResourceIdentifier = Optional(
            resourceIdentifier,
            MaximumResourceIdentifierLength,
            nameof(resourceIdentifier));
        CorrelationIdentifier = Optional(
            correlationIdentifier,
            MaximumCorrelationIdentifierLength,
            nameof(correlationIdentifier));
    }

    public Guid Id { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }
    public AuditActorKind ActorKind { get; private set; }
    public string ActorIdentifier { get; private set; } = string.Empty;
    public string Source { get; private set; } = string.Empty;
    public string Action { get; private set; } = string.Empty;
    public AuditOutcome Outcome { get; private set; }
    public string ResourceType { get; private set; } = string.Empty;
    public string? ResourceIdentifier { get; private set; }
    public string? CorrelationIdentifier { get; private set; }

    private static string Require(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return normalized;
    }

    private static string RequireMachineName(
        string value,
        int maximumLength,
        string parameterName)
    {
        var normalized = Require(value, maximumLength, parameterName);
        if (normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            throw new ArgumentException("A machine-readable name is required.", parameterName);
        }

        return normalized.ToLowerInvariant();
    }

    private static string? Optional(string? value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Require(value, maximumLength, parameterName);
    }
}
