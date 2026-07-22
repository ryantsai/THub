using Microsoft.EntityFrameworkCore;
using THub.Application.Scheduling;
using THub.Domain.Runs;
using THub.Domain.Workflows;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Scheduling;

public sealed class SqlScheduledWorkflowRunEnqueuer(
    IDbContextFactory<THubDbContext> contextFactory,
    ScheduleCalculator scheduleCalculator) : IScheduledWorkflowRunEnqueuer
{
    public async Task<ScheduledRunEnqueueResult> EnqueueAsync(
        Guid workflowId,
        int expectedWorkflowVersion,
        DateTimeOffset scheduledForUtc,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var workflow = await db.Workflows.SingleOrDefaultAsync(
            candidate => candidate.Id == workflowId,
            cancellationToken);

        if (workflow is null
            || workflow.Status != WorkflowStatus.Published
            || workflow.Version != expectedWorkflowVersion
            || string.IsNullOrWhiteSpace(workflow.CronExpression))
        {
            return new ScheduledRunEnqueueResult(
                ScheduledRunEnqueueStatus.NotEligible,
                null,
                workflow?.NextRunAtUtc);
        }

        var nextRunAtUtc = scheduleCalculator.GetNextOccurrence(
            workflow.CronExpression,
            workflow.TimeZoneId,
            evaluatedAtUtc);
        workflow.MarkScheduled(nextRunAtUtc);

        var duplicate = await db.WorkflowRuns.AnyAsync(
            run => run.WorkflowId == workflowId
                && run.WorkflowVersion == expectedWorkflowVersion
                && run.ScheduledForUtc == scheduledForUtc,
            cancellationToken);

        if (duplicate)
        {
            await db.SaveChangesAsync(cancellationToken);
            return new ScheduledRunEnqueueResult(
                ScheduledRunEnqueueStatus.Duplicate,
                null,
                nextRunAtUtc);
        }

        var run = new WorkflowRun(
            workflowId,
            expectedWorkflowVersion,
            "quartz",
            scheduledForUtc);
        db.WorkflowRuns.Add(run);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.Entry(run).State = EntityState.Detached;
            if (!await db.WorkflowRuns.AnyAsync(
                    candidate => candidate.WorkflowId == workflowId
                        && candidate.WorkflowVersion == expectedWorkflowVersion
                        && candidate.ScheduledForUtc == scheduledForUtc,
                    cancellationToken))
            {
                throw;
            }

            return new ScheduledRunEnqueueResult(
                ScheduledRunEnqueueStatus.Duplicate,
                null,
                nextRunAtUtc);
        }

        return new ScheduledRunEnqueueResult(
            ScheduledRunEnqueueStatus.Enqueued,
            run.Id,
            nextRunAtUtc);
    }
}
