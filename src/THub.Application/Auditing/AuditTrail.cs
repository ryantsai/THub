using THub.Domain.Auditing;

namespace THub.Application.Auditing;

public sealed record AuditRecordListRequest(
    int Offset = 0,
    int Limit = 100,
    string? Search = null,
    AuditOutcome? Outcome = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null);

public sealed record AuditRecordListFilter(
    int Offset,
    int Limit,
    string? Search,
    AuditOutcome? Outcome,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc);

public sealed record AuditRecordDto(
    Guid Id,
    DateTimeOffset OccurredAtUtc,
    AuditActorKind ActorKind,
    string ActorIdentifier,
    string Source,
    string Action,
    AuditOutcome Outcome,
    string ResourceType,
    string? ResourceIdentifier,
    string? CorrelationIdentifier);

public sealed record AuditRecordListPage(
    IReadOnlyList<AuditRecordDto> Items,
    int TotalCount,
    int Offset,
    int Limit);

public interface IAuditRecordStore
{
    Task<AuditRecordListPage> ListAsync(
        AuditRecordListFilter filter,
        CancellationToken cancellationToken);

    Task<AuditRecordDto?> FindAsync(Guid id, CancellationToken cancellationToken);
}

public interface IAuditViewerAuthorization
{
    Task<bool> CanViewAsync(CancellationToken cancellationToken);
}

public sealed class AuditTrailService(
    IAuditRecordStore store,
    IAuditViewerAuthorization authorization)
{
    public async Task<AuditRecordListPage> ListAsync(
        AuditRecordListRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Offset < 0 || request.Limit is < 1 or > 250)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        if (request.Search?.Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        if (request.Outcome is { } outcome && !Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        var from = request.FromUtc?.ToUniversalTime();
        var to = request.ToUtc?.ToUniversalTime();
        if (from is not null && to is not null && from > to)
        {
            throw new ArgumentException("The audit start time must not be after the end time.", nameof(request));
        }

        await DemandViewPermissionAsync(cancellationToken);
        return await store.ListAsync(
            new AuditRecordListFilter(
                request.Offset,
                request.Limit,
                string.IsNullOrWhiteSpace(request.Search) ? null : request.Search.Trim(),
                request.Outcome,
                from,
                to),
            cancellationToken);
    }

    public async Task<AuditRecordDto?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An audit identifier is required.", nameof(id));
        }

        await DemandViewPermissionAsync(cancellationToken);
        return await store.FindAsync(id, cancellationToken);
    }

    private async Task DemandViewPermissionAsync(CancellationToken cancellationToken)
    {
        if (!await authorization.CanViewAsync(cancellationToken))
        {
            throw new UnauthorizedAccessException("Audit viewing permission is required.");
        }
    }
}
