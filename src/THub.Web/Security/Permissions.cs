namespace THub.Web.Security;

public static class Permissions
{
    public const string WorkflowView = Application.Security.SecurityPermissions.WorkflowView;
    public const string WorkflowCreate = Application.Security.SecurityPermissions.WorkflowCreate;
    public const string WorkflowEdit = Application.Security.SecurityPermissions.WorkflowEdit;
    public const string WorkflowPublish = Application.Security.SecurityPermissions.WorkflowPublish;
    public const string WorkflowExecute = Application.Security.SecurityPermissions.WorkflowExecute;
    public const string WorkflowDelete = Application.Security.SecurityPermissions.WorkflowDelete;
    public const string WorkflowTargetUpsert = Application.Security.SecurityPermissions.WorkflowTargetUpsert;
    public const string WorkflowTargetDelete = Application.Security.SecurityPermissions.WorkflowTargetDelete;
    public const string RunView = Application.Security.SecurityPermissions.RunView;
    public const string ScheduleManage = Application.Security.SecurityPermissions.ScheduleManage;
    public const string ConnectionView = Application.Security.SecurityPermissions.ConnectionView;
    public const string ConnectionManage = Application.Security.SecurityPermissions.ConnectionManage;
    public const string PublicationView = Application.Security.SecurityPermissions.PublicationView;
    public const string PublicationManage = Application.Security.SecurityPermissions.PublicationManage;
    public const string SecurityManage = Application.Security.SecurityPermissions.SecurityManage;
    public const string Administration = Application.Security.SecurityPermissions.Administration;

    public static IReadOnlySet<string> All => Application.Security.SecurityPermissions.All;
}
