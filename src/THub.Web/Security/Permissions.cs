namespace THub.Web.Security;

public static class Permissions
{
    public const string WorkflowView = "workflow.view";
    public const string WorkflowEdit = "workflow.edit";
    public const string WorkflowExecute = "workflow.execute";
    public const string ScheduleManage = "schedule.manage";
    public const string ConnectionManage = "connection.manage";
    public const string PublicationManage = "publication.manage";
    public const string Administration = "administration";

    public static readonly string[] All =
    [
        WorkflowView,
        WorkflowEdit,
        WorkflowExecute,
        ScheduleManage,
        ConnectionManage,
        PublicationManage,
        Administration
    ];
}

