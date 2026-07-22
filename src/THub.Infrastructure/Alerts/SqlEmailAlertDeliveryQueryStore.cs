using Microsoft.EntityFrameworkCore;
using THub.Application.Alerts;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Alerts;

public sealed class SqlEmailAlertDeliveryQueryStore(
    IDbContextFactory<THubDbContext> contextFactory) : IEmailAlertDeliveryQueryStore
{
    private readonly IDbContextFactory<THubDbContext> _contextFactory =
        contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    public async Task<(IReadOnlyList<EmailAlertDeliveryListItem> Items, int TotalCount)> ListAsync(
        EmailAlertDeliveryListFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.AlertDeliveries.AsNoTracking().AsQueryable();
        if (filter.Status is { } status)
        {
            query = query.Where(delivery => delivery.Status == status);
        }

        if (filter.EmailDeliveryProfileId is { } profileId)
        {
            query = query.Where(delivery => delivery.EmailDeliveryProfileId == profileId);
        }

        if (filter.WorkflowRunId is { } runId)
        {
            query = query.Where(delivery => delivery.WorkflowRunId == runId);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var deliveries = await query
            .OrderByDescending(delivery => delivery.CreatedAtUtc)
            .ThenByDescending(delivery => delivery.Id)
            .Skip(filter.Offset)
            .Take(filter.Limit)
            .ToListAsync(cancellationToken);
        var profileIds = deliveries
            .Select(delivery => delivery.EmailDeliveryProfileId)
            .Distinct()
            .ToArray();
        var profileNames = await db.EmailDeliveryProfiles
            .AsNoTracking()
            .Where(profile => profileIds.Contains(profile.Id))
            .ToDictionaryAsync(profile => profile.Id, profile => profile.Name, cancellationToken);
        var items = deliveries.Select(delivery => new EmailAlertDeliveryListItem(
            delivery.Id,
            delivery.WorkflowRunId,
            delivery.EmailDeliveryProfileId,
            profileNames.GetValueOrDefault(delivery.EmailDeliveryProfileId, "Unavailable profile"),
            delivery.Source,
            delivery.Event,
            delivery.WorkflowAlertRuleId,
            delivery.WorkflowStepRunId,
            delivery.WorkflowNodeId,
            delivery.Status,
            delivery.AttemptCount,
            delivery.MaximumAttempts,
            delivery.CreatedAtUtc,
            delivery.NextAttemptAtUtc,
            delivery.LastAttemptAtUtc,
            delivery.CompletedAtUtc,
            delivery.LeaseExpiresAtUtc,
            delivery.ProviderMessageId,
            delivery.LastError?.Code,
            delivery.LastError?.Category,
            delivery.LastError?.Summary)).ToArray();
        return (items, totalCount);
    }
}
