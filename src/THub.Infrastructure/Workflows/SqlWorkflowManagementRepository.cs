using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using THub.Application.Workflows.Management;
using THub.Domain.Runs;
using THub.Domain.Workflows;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Workflows;

public sealed class SqlWorkflowManagementRepository(
    IDbContextFactory<THubDbContext> contextFactory) : IWorkflowManagementRepository
{
    public async Task<WorkflowListPage> ListWorkflowsAsync(
        WorkflowListFilter filter,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Workflows.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var pattern = $"%{EscapeLikePattern(filter.Search)}%";
            query = query.Where(workflow =>
                EF.Functions.Like(workflow.Name, pattern, "\\")
                || EF.Functions.Like(workflow.Description, pattern, "\\"));
        }
        if (filter.Status is { } status)
        {
            query = query.Where(workflow => workflow.Status == status);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(workflow => workflow.UpdatedAtUtc)
            .ThenBy(workflow => workflow.Name)
            .Skip(filter.Offset)
            .Take(filter.Limit)
            .Select(workflow => new WorkflowListRecord(
                workflow.Id,
                workflow.Name,
                workflow.Description,
                workflow.Owner,
                workflow.Status,
                workflow.Version,
                workflow.DraftRevision,
                workflow.PublishedVersionNumber,
                workflow.CronExpression,
                workflow.TimeZoneId,
                workflow.NextRunAtUtc,
                workflow.UpdatedAtUtc))
            .ToListAsync(cancellationToken);
        return new WorkflowListPage(items, totalCount);
    }

    public async Task<WorkflowDefinition?> GetWorkflowAsync(
        Guid workflowId,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Workflows
            .AsNoTracking()
            .SingleOrDefaultAsync(workflow => workflow.Id == workflowId, cancellationToken);
    }

    public async Task<WorkflowVersion?> GetWorkflowVersionAsync(
        Guid workflowVersionId,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.WorkflowVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(version => version.Id == workflowVersionId, cancellationToken);
    }

    public async Task<WorkflowRun?> GetRunAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.WorkflowRuns
            .AsNoTracking()
            .SingleOrDefaultAsync(run => run.Id == runId, cancellationToken);
    }

    public async Task<WorkflowStoreWriteResult> CreateWorkflowAsync(
        WorkflowDefinition workflow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Workflows.Add(workflow);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return WorkflowStoreWriteResult.Success;
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            return WorkflowStoreWriteResult.Conflict(
                "workflow.create.conflict",
                "The workflow conflicts with an existing record.");
        }
    }

    public async Task<WorkflowStoreWriteResult> SaveWorkflowAsync(
        WorkflowDefinition workflow,
        int expectedDraftRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var current = await db.Workflows.SingleOrDefaultAsync(
            candidate => candidate.Id == workflow.Id,
            cancellationToken);
        if (current is null)
        {
            return WorkflowStoreWriteResult.NotFound("The workflow was not found.");
        }
        if (current.DraftRevision != expectedDraftRevision)
        {
            return WorkflowStoreWriteResult.Concurrency(current.DraftRevision);
        }

        db.Entry(current).CurrentValues.SetValues(workflow);
        return await SaveTrackedWorkflowAsync(db, current, cancellationToken);
    }

    public async Task<WorkflowStoreWriteResult> PublishWorkflowAsync(
        WorkflowDefinition workflow,
        WorkflowVersion version,
        int expectedDraftRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(version);
        return await THubDbExecution.ExecuteAsync(
            contextFactory,
            async operationToken =>
            {
                await using var db = await contextFactory.CreateDbContextAsync(operationToken);
                await using var transaction = await db.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    operationToken);
                var current = await db.Workflows.SingleOrDefaultAsync(
                    candidate => candidate.Id == workflow.Id,
                    operationToken);
                if (current is null)
                {
                    return WorkflowStoreWriteResult.NotFound("The workflow was not found.");
                }
                if (current.DraftRevision != expectedDraftRevision)
                {
                    return WorkflowStoreWriteResult.Concurrency(current.DraftRevision);
                }
                if (await db.WorkflowVersions.AnyAsync(
                        candidate => candidate.WorkflowId == version.WorkflowId
                            && candidate.Version == version.Version,
                        operationToken))
                {
                    return WorkflowStoreWriteResult.Conflict(
                        "workflow.version.exists",
                        "That immutable workflow version already exists.");
                }

                db.WorkflowVersions.Add(version);
                db.Entry(current).CurrentValues.SetValues(workflow);
                var result = await SaveTrackedWorkflowAsync(db, current, operationToken);
                if (result.Status == WorkflowStoreWriteStatus.Succeeded)
                {
                    await transaction.CommitAsync(operationToken);
                }

                return result;
            },
            cancellationToken);
    }

    public async Task<WorkflowStoreWriteResult> ResumeWorkflowAsync(
        WorkflowDefinition workflow,
        int expectedDraftRevision,
        CancellationToken cancellationToken) =>
        await SaveWorkflowAsync(
            workflow,
            expectedDraftRevision,
            cancellationToken);

    public async Task<WorkflowStoreWriteResult> DeleteWorkflowAsync(
        Guid workflowId,
        int expectedDraftRevision,
        CancellationToken cancellationToken)
    {
        return await THubDbExecution.ExecuteAsync(
            contextFactory,
            async operationToken =>
            {
                await using var db = await contextFactory.CreateDbContextAsync(operationToken);
                await using var transaction = await db.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    operationToken);
                var workflow = await db.Workflows.SingleOrDefaultAsync(
                    candidate => candidate.Id == workflowId,
                    operationToken);
                if (workflow is null)
                {
                    return WorkflowStoreWriteResult.NotFound("The workflow was not found.");
                }
                if (workflow.DraftRevision != expectedDraftRevision)
                {
                    return WorkflowStoreWriteResult.Concurrency(workflow.DraftRevision);
                }
                if (workflow.Status != WorkflowStatus.Draft
                    || workflow.PublishedVersionId is not null
                    || await db.WorkflowVersions.AnyAsync(
                        version => version.WorkflowId == workflowId,
                        operationToken)
                    || await db.WorkflowRuns.AnyAsync(
                        run => run.WorkflowId == workflowId,
                        operationToken)
                    || await db.WorkflowAlertRules.AnyAsync(
                        rule => rule.WorkflowId == workflowId,
                        operationToken))
                {
                    return WorkflowStoreWriteResult.Conflict(
                        "workflow.delete.requires-unused-draft",
                        "Only an unpublished draft without versions, runs, or alert rules can be permanently deleted.");
                }

                await db.AccessResourceGrants
                    .Where(grant => grant.ResourceKind == THub.Domain.Security.AccessResourceKind.Workflow
                        && grant.ResourceId == workflowId)
                    .ExecuteDeleteAsync(operationToken);
                db.Workflows.Remove(workflow);
                await db.SaveChangesAsync(operationToken);
                await transaction.CommitAsync(operationToken);
                return WorkflowStoreWriteResult.Success;
            },
            cancellationToken);
    }

    public async Task<WorkflowStoreWriteResult> QueueRunAsync(
        WorkflowRun run,
        Guid expectedWorkflowVersionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        return await THubDbExecution.ExecuteAsync(
            contextFactory,
            async operationToken =>
            {
                await using var db = await contextFactory.CreateDbContextAsync(operationToken);
                await using var transaction = await db.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    operationToken);
                var workflowState = await db.Workflows
                    .Where(workflow => workflow.Id == run.WorkflowId)
                    .Select(workflow => new { workflow.Status, workflow.PublishedVersionId })
                    .SingleOrDefaultAsync(operationToken);
                if (workflowState is null)
                {
                    return WorkflowStoreWriteResult.NotFound("The workflow was not found.");
                }
                if (workflowState.Status != WorkflowStatus.Published
                    || workflowState.PublishedVersionId != expectedWorkflowVersionId)
                {
                    return WorkflowStoreWriteResult.Conflict(
                        "workflow.version.changed",
                        "The published workflow version changed before the run was queued.");
                }

                var hasActiveRun = await db.WorkflowRuns.AnyAsync(
                    candidate => candidate.WorkflowId == run.WorkflowId
                        && (candidate.Status == WorkflowRunStatus.Queued
                            || candidate.Status == WorkflowRunStatus.Running),
                    operationToken);
                if (hasActiveRun)
                {
                    return WorkflowStoreWriteResult.Conflict(
                        "workflow.run.active",
                        "This workflow already has an active run.");
                }

                db.WorkflowRuns.Add(run);
                try
                {
                    await db.SaveChangesAsync(operationToken);
                    await transaction.CommitAsync(operationToken);
                    return WorkflowStoreWriteResult.Success;
                }
                catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
                {
                    return WorkflowStoreWriteResult.Conflict(
                        "workflow.run.duplicate",
                        "An equivalent workflow run already exists.");
                }
            },
            cancellationToken);
    }

    public async Task<WorkflowStoreWriteResult> SaveRunCancellationAsync(
        WorkflowRun run,
        WorkflowRunStatus expectedStatus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var current = await db.WorkflowRuns.SingleOrDefaultAsync(
            candidate => candidate.Id == run.Id,
            cancellationToken);
        if (current is null)
        {
            return WorkflowStoreWriteResult.NotFound("The workflow run was not found.");
        }
        if (current.Status != expectedStatus)
        {
            return WorkflowStoreWriteResult.Concurrency();
        }

        db.Entry(current).CurrentValues.SetValues(run);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return WorkflowStoreWriteResult.Success;
        }
        catch (DbUpdateConcurrencyException)
        {
            return WorkflowStoreWriteResult.Concurrency();
        }
    }

    private static async Task<WorkflowStoreWriteResult> SaveTrackedWorkflowAsync(
        THubDbContext db,
        WorkflowDefinition workflow,
        CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return WorkflowStoreWriteResult.Success;
        }
        catch (DbUpdateConcurrencyException)
        {
            return WorkflowStoreWriteResult.Concurrency(workflow.DraftRevision);
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            return WorkflowStoreWriteResult.Conflict(
                "workflow.persistence.conflict",
                "The workflow conflicts with an existing record.");
        }
    }

    private static string EscapeLikePattern(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };
}
