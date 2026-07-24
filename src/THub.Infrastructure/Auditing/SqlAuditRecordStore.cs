using Microsoft.EntityFrameworkCore;
using THub.Application.Auditing;
using THub.Infrastructure.Persistence;

namespace THub.Infrastructure.Auditing;

public sealed class SqlAuditRecordStore(
    IDbContextFactory<THubDbContext> contextFactory) : IAuditRecordStore
{
    public async Task<AuditRecordListPage> ListAsync(
        AuditRecordListFilter filter,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.AuditRecords.AsNoTracking();
        if (filter.Outcome is { } outcome)
        {
            query = query.Where(record => record.Outcome == outcome);
        }

        if (filter.FromUtc is { } from)
        {
            query = query.Where(record => record.OccurredAtUtc >= from);
        }

        if (filter.ToUtc is { } to)
        {
            query = query.Where(record => record.OccurredAtUtc <= to);
        }

        if (filter.Search is { } search)
        {
            var pattern = $"%{EscapeLikePattern(search)}%";
            query = query.Where(record =>
                EF.Functions.Like(record.ActorIdentifier, pattern, "\\") ||
                EF.Functions.Like(record.Action, pattern, "\\") ||
                EF.Functions.Like(record.ResourceType, pattern, "\\") ||
                (record.ResourceIdentifier != null &&
                    EF.Functions.Like(record.ResourceIdentifier, pattern, "\\")) ||
                (record.CorrelationIdentifier != null &&
                    EF.Functions.Like(record.CorrelationIdentifier, pattern, "\\")));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(record => record.OccurredAtUtc)
            .ThenByDescending(record => record.Id)
            .Skip(filter.Offset)
            .Take(filter.Limit)
            .Select(record => new AuditRecordDto(
                record.Id,
                record.OccurredAtUtc,
                record.ActorKind,
                record.ActorIdentifier,
                record.Source,
                record.Action,
                record.Outcome,
                record.ResourceType,
                record.ResourceIdentifier,
                record.CorrelationIdentifier))
            .ToListAsync(cancellationToken);
        return new AuditRecordListPage(items, totalCount, filter.Offset, filter.Limit);
    }

    public async Task<AuditRecordDto?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.AuditRecords
            .AsNoTracking()
            .Where(record => record.Id == id)
            .Select(record => new AuditRecordDto(
                record.Id,
                record.OccurredAtUtc,
                record.ActorKind,
                record.ActorIdentifier,
                record.Source,
                record.Action,
                record.Outcome,
                record.ResourceType,
                record.ResourceIdentifier,
                record.CorrelationIdentifier))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
