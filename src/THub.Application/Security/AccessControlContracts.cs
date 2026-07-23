using THub.Domain.Security;

namespace THub.Application.Security;

public static class SecurityPermissions
{
    public const string WorkflowView = "workflow.view";
    public const string WorkflowCreate = "workflow.create";
    public const string WorkflowEdit = "workflow.edit";
    public const string WorkflowPublish = "workflow.publish";
    public const string WorkflowExecute = "workflow.execute";
    public const string WorkflowDelete = "workflow.delete";
    public const string WorkflowTargetUpsert = "workflow.target.upsert";
    public const string WorkflowTargetDelete = "workflow.target.delete";
    public const string RunView = "run.view";
    public const string ScheduleManage = "schedule.manage";
    public const string ConnectionView = "connection.view";
    public const string ConnectionManage = "connection.manage";
    public const string PublicationView = "publication.view";
    public const string PublicationManage = "publication.manage";
    public const string PublicationInsert = "publication.insert";
    public const string PublicationUpdate = "publication.update";
    public const string PublicationDelete = "publication.delete";
    public const string PublicationApprove = "publication.approve";
    public const string SecurityManage = "security.manage";
    public const string Administration = "administration";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
    [
        WorkflowView,
        WorkflowCreate,
        WorkflowEdit,
        WorkflowPublish,
        WorkflowExecute,
        WorkflowDelete,
        WorkflowTargetUpsert,
        WorkflowTargetDelete,
        RunView,
        ScheduleManage,
        ConnectionView,
        ConnectionManage,
        PublicationView,
        PublicationManage,
        PublicationInsert,
        PublicationUpdate,
        PublicationDelete,
        PublicationApprove,
        SecurityManage,
        Administration,
    ], StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> GlobalAssignable = new HashSet<string>(
    [
        WorkflowView,
        WorkflowCreate,
        WorkflowEdit,
        WorkflowPublish,
        WorkflowExecute,
        WorkflowDelete,
        WorkflowTargetUpsert,
        WorkflowTargetDelete,
        RunView,
        ScheduleManage,
        ConnectionView,
        ConnectionManage,
        PublicationManage,
        SecurityManage,
        Administration,
    ], StringComparer.Ordinal);

    public static readonly IReadOnlyDictionary<AccessResourceKind, IReadOnlySet<string>> ResourcePermissions =
        new Dictionary<AccessResourceKind, IReadOnlySet<string>>
        {
            [AccessResourceKind.Workflow] = new HashSet<string>(
            [
                WorkflowView,
                WorkflowEdit,
                WorkflowPublish,
                WorkflowExecute,
                WorkflowDelete,
                WorkflowTargetUpsert,
                WorkflowTargetDelete,
                ScheduleManage,
            ], StringComparer.Ordinal),
            [AccessResourceKind.Connection] = new HashSet<string>(
            [
                ConnectionView,
                ConnectionManage,
            ], StringComparer.Ordinal),
        };

    public static readonly IReadOnlySet<string> DeveloperDefaults = new HashSet<string>(
    [
        WorkflowView,
        WorkflowCreate,
        WorkflowEdit,
        WorkflowPublish,
        WorkflowExecute,
        WorkflowDelete,
        WorkflowTargetUpsert,
        WorkflowTargetDelete,
        RunView,
        ScheduleManage,
        ConnectionView,
    ], StringComparer.Ordinal);
}

public sealed record AccessRoleDto(
    Guid Id,
    string Name,
    string Description,
    SystemRoleKind? SystemRole,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<AccessRoleAssignmentDto> Assignments,
    IReadOnlyList<AccessResourceGrantDto> ResourceGrants);

public sealed record AccessRoleAssignmentDto(
    Guid Id,
    AccessPrincipalKind PrincipalKind,
    string PrincipalName);

public sealed record AccessResourceGrantDto(
    Guid Id,
    AccessResourceKind ResourceKind,
    Guid ResourceId,
    string Permission);

public sealed record AccessControlSnapshot(
    IReadOnlyList<AccessRoleDto> Roles);

public sealed record SaveAccessRoleCommand(
    Guid? RoleId,
    string Name,
    string Description,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<SaveAccessRoleAssignmentCommand> Assignments,
    IReadOnlyList<SaveAccessResourceGrantCommand> ResourceGrants,
    string Actor);

public sealed record SaveAccessRoleAssignmentCommand(
    AccessPrincipalKind PrincipalKind,
    string PrincipalName);

public sealed record SaveAccessResourceGrantCommand(
    AccessResourceKind ResourceKind,
    Guid ResourceId,
    string Permission);

public enum AccessRoleWriteStatus
{
    Saved,
    NotFound,
    Conflict,
    SystemRoleImmutable,
}

public interface IAccessControlStore
{
    Task<AccessControlSnapshot> LoadAsync(CancellationToken cancellationToken);

    Task<AccessRoleWriteStatus> SaveCustomRoleAsync(
        AccessRole role,
        IReadOnlyList<AccessRolePermission> permissions,
        IReadOnlyList<AccessRoleAssignment> assignments,
        IReadOnlyList<AccessResourceGrant> resourceGrants,
        CancellationToken cancellationToken);

    Task<AccessRoleWriteStatus> DeleteCustomRoleAsync(
        Guid roleId,
        CancellationToken cancellationToken);

    Task<AccessRoleWriteStatus> ReplaceAssignmentsAsync(
        Guid roleId,
        IReadOnlyList<AccessRoleAssignment> assignments,
        CancellationToken cancellationToken);
}
