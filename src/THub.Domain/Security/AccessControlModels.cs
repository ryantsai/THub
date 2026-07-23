namespace THub.Domain.Security;

public static class SystemRoleIds
{
    public static readonly Guid SystemAdministrator = new("10000000-0000-0000-0000-000000000001");
    public static readonly Guid Developer = new("10000000-0000-0000-0000-000000000002");
}

public enum SystemRoleKind
{
    SystemAdministrator,
    Developer,
}

public enum AccessPrincipalKind
{
    User,
    WindowsGroup,
}

public enum AccessResourceKind
{
    Workflow,
    Connection,
}

public sealed class AccessRole
{
    public const int MaximumNameLength = 100;
    public const int MaximumDescriptionLength = 500;

    private AccessRole()
    {
    }

    public AccessRole(
        Guid id,
        string name,
        string description,
        SystemRoleKind? systemRole,
        DateTimeOffset createdAtUtc,
        string createdBy)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A role identifier is required.", nameof(id));
        }

        Id = id;
        Name = RequireText(name, MaximumNameLength, nameof(name));
        Description = RequireText(description, MaximumDescriptionLength, nameof(description));
        if (systemRole is not null && !Enum.IsDefined(systemRole.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(systemRole));
        }

        SystemRole = systemRole;
        CreatedAtUtc = createdAtUtc;
        CreatedBy = RequireText(createdBy, 256, nameof(createdBy));
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public SystemRoleKind? SystemRole { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public string CreatedBy { get; private set; } = string.Empty;

    public void Rename(string name, string description)
    {
        if (SystemRole is not null)
        {
            throw new InvalidOperationException("Built-in system roles cannot be renamed.");
        }

        Name = RequireText(name, MaximumNameLength, nameof(name));
        Description = RequireText(description, MaximumDescriptionLength, nameof(description));
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
}

public sealed class AccessRolePermission
{
    public const int MaximumPermissionLength = 100;

    private AccessRolePermission()
    {
    }

    public AccessRolePermission(Guid id, Guid roleId, string permission)
    {
        Id = RequireId(id, nameof(id));
        RoleId = RequireId(roleId, nameof(roleId));
        Permission = RequirePermission(permission);
    }

    public Guid Id { get; private set; }
    public Guid RoleId { get; private set; }
    public string Permission { get; private set; } = string.Empty;

    private static string RequirePermission(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > MaximumPermissionLength ||
            normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            throw new ArgumentException("The permission name is invalid.", nameof(value));
        }

        return normalized;
    }

    private static Guid RequireId(Guid value, string parameterName) =>
        value == Guid.Empty ? throw new ArgumentException("An identifier is required.", parameterName) : value;
}

public sealed class AccessRoleAssignment
{
    public const int MaximumPrincipalNameLength = 256;

    private AccessRoleAssignment()
    {
    }

    public AccessRoleAssignment(
        Guid id,
        Guid roleId,
        AccessPrincipalKind principalKind,
        string principalName)
    {
        Id = RequireId(id, nameof(id));
        RoleId = RequireId(roleId, nameof(roleId));
        PrincipalKind = Enum.IsDefined(principalKind)
            ? principalKind
            : throw new ArgumentOutOfRangeException(nameof(principalKind));
        ArgumentException.ThrowIfNullOrWhiteSpace(principalName);
        PrincipalName = principalName.Trim();
        if (PrincipalName.Length > MaximumPrincipalNameLength)
        {
            throw new ArgumentOutOfRangeException(nameof(principalName));
        }

        NormalizedPrincipalName = PrincipalName.ToUpperInvariant();
    }

    public Guid Id { get; private set; }
    public Guid RoleId { get; private set; }
    public AccessPrincipalKind PrincipalKind { get; private set; }
    public string PrincipalName { get; private set; } = string.Empty;
    public string NormalizedPrincipalName { get; private set; } = string.Empty;

    private static Guid RequireId(Guid value, string parameterName) =>
        value == Guid.Empty ? throw new ArgumentException("An identifier is required.", parameterName) : value;
}

public sealed class AccessResourceGrant
{
    private AccessResourceGrant()
    {
    }

    public AccessResourceGrant(
        Guid id,
        Guid roleId,
        AccessResourceKind resourceKind,
        Guid resourceId,
        string permission)
    {
        Id = RequireId(id, nameof(id));
        RoleId = RequireId(roleId, nameof(roleId));
        ResourceKind = Enum.IsDefined(resourceKind)
            ? resourceKind
            : throw new ArgumentOutOfRangeException(nameof(resourceKind));
        ResourceId = RequireId(resourceId, nameof(resourceId));
        Permission = new AccessRolePermission(Guid.NewGuid(), roleId, permission).Permission;
    }

    public Guid Id { get; private set; }
    public Guid RoleId { get; private set; }
    public AccessResourceKind ResourceKind { get; private set; }
    public Guid ResourceId { get; private set; }
    public string Permission { get; private set; } = string.Empty;

    private static Guid RequireId(Guid value, string parameterName) =>
        value == Guid.Empty ? throw new ArgumentException("An identifier is required.", parameterName) : value;
}
