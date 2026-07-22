using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using THub.Application.Alerts;
using THub.Domain.Alerts;
using THub.Domain.Runs;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Alerts;

public sealed class SqlWorkflowTerminalAlertStore(
    IDbContextFactory<THubDbContext> contextFactory) : IWorkflowTerminalAlertStore
{
    private readonly IDbContextFactory<THubDbContext> _contextFactory =
        contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    public async Task<TerminalAlertPreparation?> LoadPreparationAsync(
        Guid workflowId,
        WorkflowRunStatus terminalStatus,
        CancellationToken cancellationToken)
    {
        var trigger = ToTrigger(terminalStatus);
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var workflowName = await db.Workflows
            .Where(workflow => workflow.Id == workflowId)
            .Select(workflow => workflow.Name)
            .SingleOrDefaultAsync(cancellationToken);
        if (workflowName is null)
        {
            return null;
        }

        var rules = await db.WorkflowAlertRules
            .AsNoTracking()
            .Where(rule => rule.WorkflowId == workflowId
                && rule.IsEnabled
                && (rule.Triggers & trigger) != WorkflowAlertTriggers.None)
            .OrderBy(rule => rule.Id)
            .ToListAsync(cancellationToken);
        if (rules.Count == 0)
        {
            return new TerminalAlertPreparation(workflowName, []);
        }

        var profileIds = rules.Select(rule => rule.EmailDeliveryProfileId).Distinct().ToArray();
        var profiles = await db.EmailDeliveryProfiles
            .AsNoTracking()
            .Where(profile => profileIds.Contains(profile.Id))
            .ToDictionaryAsync(profile => profile.Id, cancellationToken);
        var snapshots = rules
            .Where(rule => profiles.ContainsKey(rule.EmailDeliveryProfileId))
            .Select(rule => new TerminalAlertRuleSnapshot(
                rule,
                profiles[rule.EmailDeliveryProfileId]))
            .ToArray();
        return new TerminalAlertPreparation(workflowName, snapshots);
    }

    public async Task<TerminalAlertCommitStatus> CommitTerminalRunAsync(
        WorkflowRun terminalRun,
        WorkflowRunStatus expectedPreviousStatus,
        string? expectedLeaseOwner,
        IReadOnlyList<PreparedTerminalAlert> alerts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(terminalRun);
        ArgumentNullException.ThrowIfNull(alerts);
        if (!terminalRun.IsTerminal)
        {
            throw new ArgumentException("The workflow run must be terminal.", nameof(terminalRun));
        }

        if (alerts.Any(alert =>
                alert.Delivery.WorkflowRunId != terminalRun.Id
                || alert.Delivery.WorkflowAlertRuleId is null))
        {
            throw new ArgumentException(
                "Every prepared alert must belong to the terminal run and a workflow rule.",
                nameof(alerts));
        }

        return await THubDbExecution.ExecuteAsync(
            _contextFactory,
            async operationToken =>
            {
                await using var db = await _contextFactory.CreateDbContextAsync(operationToken);
                await using var transaction = await db.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    operationToken);
                var current = await db.WorkflowRuns.SingleOrDefaultAsync(
                    run => run.Id == terminalRun.Id,
                    operationToken);
                if (current is null)
                {
                    return TerminalAlertCommitStatus.NotFound;
                }

                if (current.IsTerminal)
                {
                    var committed = current.Status == terminalRun.Status
                        && await AllDeliveriesExistAsync(db, alerts, operationToken);
                    await transaction.CommitAsync(operationToken);
                    return committed
                        ? TerminalAlertCommitStatus.AlreadyCommitted
                        : TerminalAlertCommitStatus.Conflict;
                }

                if (!OwnsExpectedRunState(
                        current,
                        terminalRun,
                        expectedPreviousStatus,
                        expectedLeaseOwner))
                {
                    return TerminalAlertCommitStatus.LeaseLost;
                }

                if (!await AlertSnapshotsRemainCurrentAsync(
                        db,
                        terminalRun.WorkflowId,
                        terminalRun.Status,
                        alerts,
                        operationToken))
                {
                    return TerminalAlertCommitStatus.SnapshotChanged;
                }

                db.Entry(current).CurrentValues.SetValues(terminalRun);
                db.AlertDeliveries.AddRange(alerts.Select(alert => alert.Delivery));
                try
                {
                    await db.SaveChangesAsync(operationToken);
                    await transaction.CommitAsync(operationToken);
                    return TerminalAlertCommitStatus.Saved;
                }
                catch (DbUpdateConcurrencyException)
                {
                    return TerminalAlertCommitStatus.Conflict;
                }
                catch (DbUpdateException exception) when (IsUniqueViolation(exception))
                {
                    await transaction.RollbackAsync(operationToken);
                    await using var verificationDb =
                        await _contextFactory.CreateDbContextAsync(operationToken);
                    var storedStatus = await verificationDb.WorkflowRuns
                        .Where(run => run.Id == terminalRun.Id)
                        .Select(run => (WorkflowRunStatus?)run.Status)
                        .SingleOrDefaultAsync(operationToken);
                    return storedStatus == terminalRun.Status
                        && await AllDeliveriesExistAsync(verificationDb, alerts, operationToken)
                            ? TerminalAlertCommitStatus.AlreadyCommitted
                            : TerminalAlertCommitStatus.Conflict;
                }
                catch (DbUpdateException exception) when (IsForeignKeyViolation(exception))
                {
                    return TerminalAlertCommitStatus.SnapshotChanged;
                }
            },
            cancellationToken);
    }

    private static bool OwnsExpectedRunState(
        WorkflowRun current,
        WorkflowRun terminalRun,
        WorkflowRunStatus expectedPreviousStatus,
        string? expectedLeaseOwner)
    {
        if (current.Status != expectedPreviousStatus
            || current.WorkflowId != terminalRun.WorkflowId
            || current.WorkflowVersionId != terminalRun.WorkflowVersionId
            || current.WorkflowVersion != terminalRun.WorkflowVersion
            || current.AttemptCount != terminalRun.AttemptCount)
        {
            return false;
        }

        if (expectedPreviousStatus == WorkflowRunStatus.Queued)
        {
            return terminalRun.Status == WorkflowRunStatus.Cancelled
                && terminalRun.CancellationRequested
                && current.LeaseOwner is null
                && expectedLeaseOwner is null;
        }

        return current.CancellationRequestedAtUtc == terminalRun.CancellationRequestedAtUtc
            && string.Equals(
                current.CancellationRequestedBy,
                terminalRun.CancellationRequestedBy,
                StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(expectedLeaseOwner)
            && string.Equals(current.LeaseOwner, expectedLeaseOwner, StringComparison.Ordinal)
            && current.LeaseExpiresAtUtc is DateTimeOffset leaseExpiresAt
            && terminalRun.CompletedAtUtc is DateTimeOffset completedAt
            && leaseExpiresAt > completedAt;
    }

    private static async Task<bool> AlertSnapshotsRemainCurrentAsync(
        THubDbContext db,
        Guid workflowId,
        WorkflowRunStatus terminalStatus,
        IReadOnlyList<PreparedTerminalAlert> alerts,
        CancellationToken cancellationToken)
    {
        var trigger = ToTrigger(terminalStatus);
        var ruleIds = alerts
            .Select(alert => alert.Delivery.WorkflowAlertRuleId!.Value)
            .Distinct()
            .Order()
            .ToArray();
        var currentRuleIds = await db.WorkflowAlertRules
            .Where(rule => rule.WorkflowId == workflowId
                && rule.IsEnabled
                && (rule.Triggers & trigger) != WorkflowAlertTriggers.None)
            .Select(rule => rule.Id)
            .OrderBy(ruleId => ruleId)
            .ToArrayAsync(cancellationToken);
        if (!currentRuleIds.SequenceEqual(ruleIds))
        {
            return false;
        }

        if (ruleIds.Length == 0)
        {
            return true;
        }

        var rules = await db.WorkflowAlertRules
            .Where(rule => ruleIds.Contains(rule.Id))
            .ToDictionaryAsync(rule => rule.Id, cancellationToken);
        var profileIds = alerts
            .Select(alert => alert.Delivery.EmailDeliveryProfileId)
            .Distinct()
            .ToArray();
        var profiles = await db.EmailDeliveryProfiles
            .Where(profile => profileIds.Contains(profile.Id))
            .ToDictionaryAsync(profile => profile.Id, cancellationToken);

        foreach (var alert in alerts)
        {
            var delivery = alert.Delivery;
            if (delivery.WorkflowAlertRuleId is not Guid ruleId
                || !rules.TryGetValue(ruleId, out var rule)
                || !profiles.TryGetValue(delivery.EmailDeliveryProfileId, out var profile)
                || rule.EmailDeliveryProfileId != delivery.EmailDeliveryProfileId
                || rule.UpdatedAtUtc != alert.ExpectedRuleUpdatedAtUtc.ToUniversalTime()
                || profile.UpdatedAtUtc != alert.ExpectedProfileUpdatedAtUtc.ToUniversalTime()
                || !rule.Matches(terminalStatus))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<bool> AllDeliveriesExistAsync(
        THubDbContext db,
        IReadOnlyList<PreparedTerminalAlert> alerts,
        CancellationToken cancellationToken)
    {
        if (alerts.Count == 0)
        {
            return true;
        }

        var keys = alerts.Select(alert => alert.Delivery.DeduplicationKey).Distinct().ToArray();
        var count = await db.AlertDeliveries.CountAsync(
            delivery => keys.Contains(delivery.DeduplicationKey),
            cancellationToken);
        return count == keys.Length;
    }

    private static WorkflowAlertTriggers ToTrigger(WorkflowRunStatus status) => status switch
    {
        WorkflowRunStatus.Succeeded => WorkflowAlertTriggers.RunSucceeded,
        WorkflowRunStatus.Failed => WorkflowAlertTriggers.RunFailed,
        WorkflowRunStatus.Cancelled => WorkflowAlertTriggers.RunCancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(status), "A terminal run status is required.")
    };

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };

    private static bool IsForeignKeyViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 547 };
}
