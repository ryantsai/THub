using THub.Domain.Alerts;
using THub.Domain.Runs;

namespace THub.Application.Alerts;

public sealed record TerminalAlertRuleSnapshot(
    WorkflowAlertRule Rule,
    EmailDeliveryProfile Profile);

public sealed record TerminalAlertPreparation(
    string WorkflowName,
    IReadOnlyList<TerminalAlertRuleSnapshot> Rules);

public sealed record PreparedTerminalAlert(
    AlertDelivery Delivery,
    DateTimeOffset ExpectedRuleUpdatedAtUtc,
    DateTimeOffset ExpectedProfileUpdatedAtUtc);

public enum TerminalAlertCommitStatus
{
    Saved,
    AlreadyCommitted,
    NotFound,
    LeaseLost,
    SnapshotChanged,
    Conflict
}

public interface IWorkflowTerminalAlertStore
{
    Task<TerminalAlertPreparation?> LoadPreparationAsync(
        Guid workflowId,
        WorkflowRunStatus terminalStatus,
        CancellationToken cancellationToken);

    /// <summary>
    /// Commits the authoritative run terminal transition and all prepared delivery rows in one
    /// SQL transaction. Implementations must recheck the prior run lease/state and rule/profile
    /// revisions before saving.
    /// </summary>
    Task<TerminalAlertCommitStatus> CommitTerminalRunAsync(
        WorkflowRun terminalRun,
        WorkflowRunStatus expectedPreviousStatus,
        string? expectedLeaseOwner,
        IReadOnlyList<PreparedTerminalAlert> alerts,
        CancellationToken cancellationToken);
}

public sealed record CommitTerminalRunWithAlertsCommand(
    WorkflowRun TerminalRun,
    WorkflowRunStatus ExpectedPreviousStatus,
    string? ExpectedLeaseOwner);

public sealed record TerminalRunAlertCommitDto(
    Guid WorkflowRunId,
    WorkflowRunStatus Status,
    int DeliveryCount,
    int DeadLetteredAtEnqueue,
    bool AlreadyCommitted);
