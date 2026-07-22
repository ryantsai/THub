using System.Text;

namespace THub.Domain.Publications;

public sealed class Publication
{
    public const int MaximumSlugLength = 100;
    public const int MaximumNameLength = 200;
    public const int MaximumIdentityLength = 256;

    private Publication()
    {
    }

    public Publication(
        string slug,
        string name,
        PublicationKind kind,
        string createdBy,
        DateTimeOffset createdAtUtc)
        : this(Guid.NewGuid(), slug, name, kind, createdBy, createdAtUtc)
    {
    }

    public Publication(
        Guid id,
        string slug,
        string name,
        PublicationKind kind,
        string createdBy,
        DateTimeOffset createdAtUtc)
    {
        Id = PublicationGuard.RequireId(id, nameof(id));
        Slug = NormalizeSlug(slug);
        Name = PublicationGuard.Require(name, nameof(name), MaximumNameLength);
        Kind = PublicationGuard.RequireDefined(kind, nameof(kind));
        CreatedBy = PublicationGuard.Require(createdBy, nameof(createdBy), MaximumIdentityLength);
        CreatedAtUtc = PublicationGuard.AsUtc(createdAtUtc);
        UpdatedBy = CreatedBy;
        UpdatedAtUtc = CreatedAtUtc;
    }

    public Guid Id { get; private set; }

    public string Slug { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public PublicationKind Kind { get; private set; }

    public PublicationState State { get; private set; } = PublicationState.Draft;

    public Guid? ActiveVersionId { get; private set; }

    public string CreatedBy { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public string UpdatedBy { get; private set; } = string.Empty;

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void Rename(string name, string updatedBy, DateTimeOffset updatedAtUtc)
    {
        EnsureNotArchived();
        Name = PublicationGuard.Require(name, nameof(name), MaximumNameLength);
        Touch(updatedBy, updatedAtUtc);
    }

    public void ChangeSlug(string slug, string updatedBy, DateTimeOffset updatedAtUtc)
    {
        EnsureNotArchived();
        if (State == PublicationState.Active)
        {
            throw new InvalidOperationException("Disable an active publication before changing its route slug.");
        }

        Slug = NormalizeSlug(slug);
        Touch(updatedBy, updatedAtUtc);
    }

    public void ActivateVersion(
        PublicationVersion version,
        string updatedBy,
        DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(version);
        EnsureNotArchived();

        if (version.PublicationId != Id)
        {
            throw new ArgumentException("Version belongs to a different publication.", nameof(version));
        }

        if (PublicationGuard.AsUtc(updatedAtUtc) < version.CreatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(updatedAtUtc),
                "A version cannot be activated before it was created.");
        }

        ActiveVersionId = version.Id;
        State = PublicationState.Active;
        Touch(updatedBy, updatedAtUtc);
    }

    public void Disable(string updatedBy, DateTimeOffset updatedAtUtc)
    {
        if (State != PublicationState.Active)
        {
            throw new InvalidOperationException("Only an active publication can be disabled.");
        }

        State = PublicationState.Disabled;
        Touch(updatedBy, updatedAtUtc);
    }

    public void Archive(string updatedBy, DateTimeOffset updatedAtUtc)
    {
        EnsureNotArchived();
        if (State == PublicationState.Active)
        {
            throw new InvalidOperationException("Disable an active publication before archiving it.");
        }

        State = PublicationState.Archived;
        Touch(updatedBy, updatedAtUtc);
    }

    public static string NormalizeSlug(string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        var builder = new StringBuilder(slug.Length);
        var separatorPending = false;

        foreach (var character in slug.Trim())
        {
            if (character is >= 'A' and <= 'Z')
            {
                AppendSeparatorIfNeeded(builder, ref separatorPending);
                builder.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                AppendSeparatorIfNeeded(builder, ref separatorPending);
                builder.Append(character);
                continue;
            }

            if (character is '-' or '_' || char.IsWhiteSpace(character))
            {
                separatorPending = builder.Length > 0;
                continue;
            }

            throw new ArgumentException(
                "Slug may contain only ASCII letters, numbers, spaces, underscores, and hyphens.",
                nameof(slug));
        }

        var normalized = builder.ToString();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Slug must contain at least one letter or number.", nameof(slug));
        }

        if (normalized.Length > MaximumSlugLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slug),
                $"Slug cannot exceed {MaximumSlugLength} characters after normalization.");
        }

        return normalized;
    }

    private static void AppendSeparatorIfNeeded(StringBuilder builder, ref bool separatorPending)
    {
        if (separatorPending)
        {
            builder.Append('-');
            separatorPending = false;
        }
    }

    private void Touch(string updatedBy, DateTimeOffset updatedAtUtc)
    {
        UpdatedBy = PublicationGuard.Require(updatedBy, nameof(updatedBy), MaximumIdentityLength);
        UpdatedAtUtc = PublicationGuard.NotBefore(updatedAtUtc, UpdatedAtUtc, nameof(updatedAtUtc));
    }

    private void EnsureNotArchived()
    {
        if (State == PublicationState.Archived)
        {
            throw new InvalidOperationException("Archived publications cannot be changed.");
        }
    }
}
