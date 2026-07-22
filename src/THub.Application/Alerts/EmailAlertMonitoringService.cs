using THub.Domain.Alerts;
using THub.Domain.Runs;

namespace THub.Application.Alerts;

public sealed record EmailAlertDeliveryListRequest(
    int Offset = 0,
    int Limit = 50,
    AlertDeliveryStatus? Status = null,
    Guid? EmailDeliveryProfileId = null,
    Guid? WorkflowRunId = null);

public sealed record EmailAlertDeliveryListFilter(
    int Offset,
    int Limit,
    AlertDeliveryStatus? Status,
    Guid? EmailDeliveryProfileId,
    Guid? WorkflowRunId);

public sealed record EmailAlertDeliveryListItem(
    Guid Id,
    Guid WorkflowRunId,
    Guid EmailDeliveryProfileId,
    string ProfileName,
    AlertDeliverySource Source,
    AlertDeliveryEvent Event,
    Guid? WorkflowAlertRuleId,
    Guid? WorkflowStepRunId,
    string? WorkflowNodeId,
    AlertDeliveryStatus Status,
    int AttemptCount,
    int MaximumAttempts,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset NextAttemptAtUtc,
    DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? LeaseExpiresAtUtc,
    string? ProviderMessageId,
    string? LastErrorCode,
    ExecutionErrorCategory? LastErrorCategory,
    string? LastErrorSummary);

public sealed record EmailAlertDeliveryListPage(
    IReadOnlyList<EmailAlertDeliveryListItem> Items,
    int TotalCount,
    int Offset,
    int Limit);

public interface IEmailAlertDeliveryQueryStore
{
    Task<(IReadOnlyList<EmailAlertDeliveryListItem> Items, int TotalCount)> ListAsync(
        EmailAlertDeliveryListFilter filter,
        CancellationToken cancellationToken);
}

public sealed class EmailAlertMonitoringService(IEmailAlertDeliveryQueryStore store)
{
    public const int MaximumPageSize = 200;

    private readonly IEmailAlertDeliveryQueryStore _store =
        store ?? throw new ArgumentNullException(nameof(store));

    public async Task<AlertResult<EmailAlertDeliveryListPage>> ListAsync(
        EmailAlertDeliveryListRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null
            || request.Offset < 0
            || request.Limit is < 1 or > MaximumPageSize
            || request.EmailDeliveryProfileId == Guid.Empty
            || request.WorkflowRunId == Guid.Empty)
        {
            return AlertResults.Validation<EmailAlertDeliveryListPage>(
                "email.delivery_query_invalid",
                $"Delivery queries require a non-negative offset and a limit from 1 to {MaximumPageSize}.");
        }

        var filter = new EmailAlertDeliveryListFilter(
            request.Offset,
            request.Limit,
            request.Status,
            request.EmailDeliveryProfileId,
            request.WorkflowRunId);
        var result = await _store.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        return AlertResult<EmailAlertDeliveryListPage>.Success(new(
            result.Items,
            result.TotalCount,
            request.Offset,
            request.Limit));
    }
}
