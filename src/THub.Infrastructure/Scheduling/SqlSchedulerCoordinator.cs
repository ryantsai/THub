using System.Data;
using Microsoft.EntityFrameworkCore;
using THub.Application.Scheduling;
using THub.Domain.Runs;
using THub.Domain.Workflows;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Scheduling;

public sealed class SqlSchedulerCoordinator(
    IDbContextFactory<THubDbContext> contextFactory,
    ScheduleCalculator calculator) : ISchedulerCoordinator
{
    public async Task<int> EnqueueDueWorkflowsAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var dueWorkflows = await db.Workflows
            .Where(workflow => workflow.Status == WorkflowStatus.Published
                && workflow.NextRunAtUtc != null
                && workflow.NextRunAtUtc <= nowUtc)
            .OrderBy(workflow => workflow.NextRunAtUtc)
            .Take(25)
            .ToListAsync(cancellationToken);

        foreach (var workflow in dueWorkflows)
        {
            db.WorkflowRuns.Add(new WorkflowRun(workflow.Id, workflow.Version, "scheduler"));

            DateTimeOffset? nextRun = null;
            if (!string.IsNullOrWhiteSpace(workflow.CronExpression))
            {
                nextRun = calculator.GetNextOccurrence(
                    workflow.CronExpression,
                    workflow.TimeZoneId,
                    nowUtc);
            }

            workflow.MarkScheduled(nextRun);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return dueWorkflows.Count;
    }
}

