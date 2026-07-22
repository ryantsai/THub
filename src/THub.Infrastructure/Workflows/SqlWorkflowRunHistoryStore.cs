using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using THub.Application.Workflows.Management;
using THub.Domain.Runs;
using THub.Domain.Workflows;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Workflows;

public sealed class SqlWorkflowRunHistoryStore(
    IDbContextFactory<THubDbContext> contextFactory) : IWorkflowRunHistoryStore
{
    public async Task<WorkflowRunListPage> ListAsync(
        WorkflowRunListFilter filter,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.WorkflowRuns.AsNoTracking();
        if (filter.WorkflowId is { } workflowId)
        {
            query = query.Where(run => run.WorkflowId == workflowId);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(run => run.Status == status);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var runs = await query
            .OrderByDescending(run => run.QueuedAtUtc)
            .ThenByDescending(run => run.Id)
            .Skip(filter.Offset)
            .Take(filter.Limit)
            .ToListAsync(cancellationToken);
        var workflowIds = runs.Select(run => run.WorkflowId).Distinct().ToArray();
        var names = await db.Workflows
            .AsNoTracking()
            .Where(workflow => workflowIds.Contains(workflow.Id))
            .ToDictionaryAsync(
                workflow => workflow.Id,
                workflow => workflow.Name,
                cancellationToken);

        var records = runs.Select(run => new WorkflowRunListRecord(
            run.Id,
            run.WorkflowId,
            names.GetValueOrDefault(run.WorkflowId, "Deleted workflow"),
            run.WorkflowVersion,
            run.Status,
            run.TriggeredBy,
            run.ScheduledForUtc,
            run.QueuedAtUtc,
            run.StartedAtUtc,
            run.CompletedAtUtc,
            run.AttemptCount,
            run.CancellationRequested,
            run.Error)).ToArray();
        return new WorkflowRunListPage(records, totalCount);
    }

    public async Task<WorkflowRunDetailsDto?> GetDetailsAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var run = await db.WorkflowRuns
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == runId, cancellationToken);
        if (run is null)
        {
            return null;
        }

        var workflowName = await db.Workflows
            .AsNoTracking()
            .Where(workflow => workflow.Id == run.WorkflowId)
            .Select(workflow => workflow.Name)
            .SingleOrDefaultAsync(cancellationToken) ?? "Deleted workflow";
        var steps = await db.WorkflowStepRuns
            .AsNoTracking()
            .Where(step => step.WorkflowRunId == run.Id)
            .OrderBy(step => step.QueuedAtUtc)
            .ThenBy(step => step.NodeId)
            .ThenBy(step => step.Attempt)
            .ToListAsync(cancellationToken);

        return new WorkflowRunDetailsDto(
            run.Id,
            run.WorkflowId,
            workflowName,
            run.WorkflowVersionId,
            run.WorkflowVersion,
            run.RetryOfRunId,
            run.Status,
            run.TriggeredBy,
            run.ScheduledForUtc,
            run.QueuedAtUtc,
            run.StartedAtUtc,
            run.CompletedAtUtc,
            run.AttemptCount,
            run.LeaseOwner,
            run.LastHeartbeatAtUtc,
            run.CancellationRequestedAtUtc,
            run.CancellationRequestedBy,
            run.Error,
            steps.Select(MapStep).ToArray());
    }

    public async Task<WorkflowStoreWriteResult> QueueRetryAsync(
        WorkflowRun retryRun,
        Guid sourceRunId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retryRun);
        return await THubDbExecution.ExecuteAsync(
            contextFactory,
            async operationToken =>
            {
                await using var db = await contextFactory.CreateDbContextAsync(operationToken);
                await using var transaction = await db.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    operationToken);

                var source = await db.WorkflowRuns
                    .SingleOrDefaultAsync(run => run.Id == sourceRunId, operationToken);
                if (source is null)
                {
                    return WorkflowStoreWriteResult.NotFound("The source run was not found.");
                }

                if (source.Status != WorkflowRunStatus.Failed
                    || source.WorkflowId != retryRun.WorkflowId
                    || source.WorkflowVersionId != retryRun.WorkflowVersionId
                    || source.WorkflowVersion != retryRun.WorkflowVersion
                    || retryRun.RetryOfRunId != source.Id)
                {
                    return WorkflowStoreWriteResult.Conflict(
                        "run.retry.source-changed",
                        "The source run is no longer eligible for this exact-version retry.");
                }

                var workflowStatus = await db.Workflows
                    .Where(workflow => workflow.Id == retryRun.WorkflowId)
                    .Select(workflow => (WorkflowStatus?)workflow.Status)
                    .SingleOrDefaultAsync(operationToken);
                if (workflowStatus is null)
                {
                    return WorkflowStoreWriteResult.NotFound("The workflow was not found.");
                }

                if (workflowStatus == WorkflowStatus.Archived)
                {
                    return WorkflowStoreWriteResult.Conflict(
                        "workflow.archived",
                        "An archived workflow cannot be retried.");
                }

                var versionExists = await db.WorkflowVersions.AnyAsync(
                    version => version.Id == retryRun.WorkflowVersionId
                        && version.WorkflowId == retryRun.WorkflowId
                        && version.Version == retryRun.WorkflowVersion,
                    operationToken);
                if (!versionExists)
                {
                    return WorkflowStoreWriteResult.Conflict(
                        "run.retry.version-missing",
                        "The immutable workflow version no longer exists.");
                }

                var hasActiveRun = await db.WorkflowRuns.AnyAsync(
                    run => run.WorkflowId == retryRun.WorkflowId
                        && (run.Status == WorkflowRunStatus.Queued
                            || run.Status == WorkflowRunStatus.Running),
                    operationToken);
                if (hasActiveRun)
                {
                    return WorkflowStoreWriteResult.Conflict(
                        "workflow.run.active",
                        "This workflow already has an active run.");
                }

                db.WorkflowRuns.Add(retryRun);
                try
                {
                    await db.SaveChangesAsync(operationToken);
                    await transaction.CommitAsync(operationToken);
                    return WorkflowStoreWriteResult.Success;
                }
                catch (DbUpdateException exception) when (
                    exception.InnerException is SqlException { Number: 2601 or 2627 })
                {
                    return WorkflowStoreWriteResult.Conflict(
                        "run.retry.duplicate",
                        "An equivalent retry already exists.");
                }
                catch (DbUpdateConcurrencyException)
                {
                    return WorkflowStoreWriteResult.Concurrency();
                }
            },
            cancellationToken);
    }

    private static WorkflowStepRunDto MapStep(WorkflowStepRun step) => new(
        step.Id,
        step.NodeId,
        step.Attempt,
        step.Status,
        step.QueuedAtUtc,
        step.StartedAtUtc,
        step.CompletedAtUtc,
        step.RowsRead,
        step.RowsWritten,
        step.BatchesProcessed,
        step.BytesRead,
        step.BytesWritten,
        step.Error);
}
