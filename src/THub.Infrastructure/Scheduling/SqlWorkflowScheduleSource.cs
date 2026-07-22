using Microsoft.EntityFrameworkCore;
using THub.Application.Scheduling;
using THub.Domain.Workflows;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Scheduling;

public sealed class SqlWorkflowScheduleSource(
    IDbContextFactory<THubDbContext> contextFactory) : IWorkflowScheduleSource
{
    public async Task<IReadOnlyList<WorkflowSchedule>> GetPublishedSchedulesAsync(
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await db.Workflows
            .AsNoTracking()
            .Where(workflow => workflow.Status == WorkflowStatus.Published
                && workflow.PublishedVersionId != null
                && workflow.PublishedVersionNumber != null
                && workflow.CronExpression != null)
            .OrderBy(workflow => workflow.Id)
            .Select(workflow => new WorkflowSchedule(
                workflow.Id,
                workflow.PublishedVersionNumber!.Value,
                workflow.CronExpression!,
                workflow.TimeZoneId,
                workflow.NextRunAtUtc))
            .ToListAsync(cancellationToken);
    }
}
