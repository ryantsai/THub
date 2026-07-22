using Quartz;

namespace THub.Worker.Scheduling;

internal static class QuartzWorkflowKeys
{
    public const string ReconciliationGroup = "thub-system";
    public const string WorkflowGroup = "thub-workflows";

    public static readonly JobKey ReconciliationJob = new("reconcile-workflow-schedules", ReconciliationGroup);

    public static JobKey WorkflowJob(Guid workflowId) =>
        new($"workflow-{workflowId:N}", WorkflowGroup);

    public static TriggerKey WorkflowTrigger(Guid workflowId) =>
        new($"workflow-{workflowId:N}", WorkflowGroup);
}
