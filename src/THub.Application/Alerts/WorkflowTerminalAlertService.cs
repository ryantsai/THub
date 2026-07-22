using THub.Domain.Alerts;
using THub.Domain.Runs;

namespace THub.Application.Alerts;

public sealed class WorkflowTerminalAlertService(
    IWorkflowTerminalAlertStore store,
    TimeProvider timeProvider)
{
    private const string ValidationLeaseOwner = "thub-email-policy-validation";
    private static readonly TimeSpan ValidationLeaseDuration = TimeSpan.FromMinutes(1);

    private readonly IWorkflowTerminalAlertStore _store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<AlertResult<TerminalRunAlertCommitDto>> CommitAsync(
        CommitTerminalRunWithAlertsCommand command,
        CancellationToken cancellationToken)
    {
        if (command is null || command.TerminalRun is null)
        {
            return AlertResults.Validation<TerminalRunAlertCommitDto>(
                "email.terminal_run_required",
                "A terminal workflow run is required.");
        }

        var run = command.TerminalRun;
        if (!run.IsTerminal)
        {
            return AlertResults.Validation<TerminalRunAlertCommitDto>(
                "email.run_not_terminal",
                "Alert intent can be committed only with a terminal workflow run transition.");
        }

        if (command.ExpectedPreviousStatus is not (
                WorkflowRunStatus.Queued or WorkflowRunStatus.Running))
        {
            return AlertResults.Validation<TerminalRunAlertCommitDto>(
                "email.previous_run_state_invalid",
                "The expected previous run state must be queued or running.");
        }

        if (command.ExpectedPreviousStatus == WorkflowRunStatus.Running
            && string.IsNullOrWhiteSpace(command.ExpectedLeaseOwner))
        {
            return AlertResults.Validation<TerminalRunAlertCommitDto>(
                "email.run_lease_required",
                "A running workflow terminal transition requires its lease owner.");
        }

        if (command.ExpectedPreviousStatus == WorkflowRunStatus.Queued
            && run.Status != WorkflowRunStatus.Cancelled)
        {
            return AlertResults.Validation<TerminalRunAlertCommitDto>(
                "email.queued_terminal_state_invalid",
                "A queued workflow run can transition directly only to cancelled.");
        }

        var preparation = await _store.LoadPreparationAsync(
                run.WorkflowId,
                run.Status,
                cancellationToken)
            .ConfigureAwait(false);
        if (preparation is null)
        {
            return AlertResults.NotFound<TerminalRunAlertCommitDto>(
                "email.workflow_not_found",
                "The workflow for the terminal run was not found.");
        }

        var now = _timeProvider.GetUtcNow();
        var preparedAlerts = new List<PreparedTerminalAlert>(preparation.Rules.Count);
        var deadLettered = 0;
        foreach (var snapshot in preparation.Rules)
        {
            if (!snapshot.Rule.Matches(run.Status))
            {
                continue;
            }

            var delivery = PrepareDelivery(run, preparation.WorkflowName, snapshot, now);
            if (delivery.Status == AlertDeliveryStatus.DeadLettered)
            {
                deadLettered++;
            }

            preparedAlerts.Add(new PreparedTerminalAlert(
                delivery,
                snapshot.Rule.UpdatedAtUtc,
                snapshot.Profile.UpdatedAtUtc));
        }

        var status = await _store.CommitTerminalRunAsync(
                run,
                command.ExpectedPreviousStatus,
                command.ExpectedLeaseOwner,
                preparedAlerts,
                cancellationToken)
            .ConfigureAwait(false);
        return status switch
        {
            TerminalAlertCommitStatus.Saved =>
                Success(run, preparedAlerts.Count, deadLettered, alreadyCommitted: false),
            TerminalAlertCommitStatus.AlreadyCommitted =>
                Success(run, preparedAlerts.Count, deadLettered, alreadyCommitted: true),
            TerminalAlertCommitStatus.NotFound =>
                AlertResults.NotFound<TerminalRunAlertCommitDto>(
                    "email.run_not_found",
                    "The workflow run was not found."),
            TerminalAlertCommitStatus.LeaseLost =>
                AlertResults.Conflict<TerminalRunAlertCommitDto>(
                    "email.run_lease_lost",
                    "The workflow run lease or state changed before terminal alert intent was committed."),
            TerminalAlertCommitStatus.SnapshotChanged =>
                AlertResults.Conflict<TerminalRunAlertCommitDto>(
                    "email.alert_policy_changed",
                    "Email alert policy changed during the terminal transition. Reload and retry."),
            _ => AlertResults.Conflict<TerminalRunAlertCommitDto>(
                "email.terminal_commit_conflict",
                "The terminal workflow state and Email intent could not be committed together.")
        };
    }

    private static AlertDelivery PrepareDelivery(
        WorkflowRun run,
        string workflowName,
        TerminalAlertRuleSnapshot snapshot,
        DateTimeOffset createdAtUtc)
    {
        EmailMessage message;
        ExecutionError? policyError = null;
        try
        {
            message = snapshot.Rule.Template.Render(
                snapshot.Rule.Recipients,
                CreateVariables(run, workflowName));
            snapshot.Profile.ValidateMessage(message);
        }
        catch (Exception exception) when (AlertResults.IsDomainException(exception))
        {
            message = new EmailMessage(
                snapshot.Rule.Recipients,
                "THub Email alert could not be rendered",
                $"Workflow run {run.Id:D} reached {run.Status}, but its Email alert policy is invalid.");
            policyError = new ExecutionError(
                snapshot.Profile.IsEnabled
                    ? "email.alert_policy_invalid"
                    : "email.profile_disabled",
                ExecutionErrorCategory.Configuration,
                snapshot.Profile.IsEnabled
                    ? "The Email alert did not satisfy its delivery profile policy."
                    : "The Email delivery profile is disabled.",
                isRetryable: false);
        }

        var delivery = AlertDelivery.ForWorkflowRule(
            snapshot.Rule.Id,
            run.Id,
            snapshot.Profile.Id,
            ToDeliveryEvent(run.Status),
            message,
            createdAtUtc);
        if (policyError is not null)
        {
            _ = delivery.TryClaim(
                ValidationLeaseOwner,
                createdAtUtc,
                ValidationLeaseDuration);
            delivery.RecordFailure(
                ValidationLeaseOwner,
                policyError,
                createdAtUtc);
        }

        return delivery;
    }

    private static IReadOnlyDictionary<string, string?> CreateVariables(
        WorkflowRun run,
        string workflowName) => new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["workflow.id"] = run.WorkflowId.ToString("D"),
            ["workflow.name"] = workflowName,
            ["run.id"] = run.Id.ToString("D"),
            ["run.status"] = run.Status.ToString(),
            ["run.triggeredBy"] = run.TriggeredBy,
            ["run.startedAtUtc"] = run.StartedAtUtc?.ToString("O"),
            ["run.completedAtUtc"] = run.CompletedAtUtc?.ToString("O"),
            ["error.code"] = run.Error?.Code,
            ["error.category"] = run.Error?.Category.ToString(),
            ["error.summary"] = run.Error?.Summary
        };

    private static AlertDeliveryEvent ToDeliveryEvent(WorkflowRunStatus status) => status switch
    {
        WorkflowRunStatus.Succeeded => AlertDeliveryEvent.RunSucceeded,
        WorkflowRunStatus.Failed => AlertDeliveryEvent.RunFailed,
        WorkflowRunStatus.Cancelled => AlertDeliveryEvent.RunCancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private static AlertResult<TerminalRunAlertCommitDto> Success(
        WorkflowRun run,
        int deliveryCount,
        int deadLettered,
        bool alreadyCommitted) => AlertResult<TerminalRunAlertCommitDto>.Success(new(
            run.Id,
            run.Status,
            deliveryCount,
            deadLettered,
            alreadyCommitted));
}
