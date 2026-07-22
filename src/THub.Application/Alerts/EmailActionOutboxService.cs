using THub.Domain.Alerts;

namespace THub.Application.Alerts;

public sealed record QueueEmailActionCommand(
    Guid WorkflowRunId,
    Guid WorkflowStepRunId,
    string WorkflowNodeId,
    Guid EmailDeliveryProfileId,
    IReadOnlyList<string> Recipients,
    string SubjectTemplate,
    string BodyTemplate,
    IReadOnlyDictionary<string, string?> Variables,
    int MaximumAttempts = 5);

public sealed record QueuedEmailActionDto(
    Guid DeliveryId,
    string DeduplicationKey,
    bool AlreadyExisted);

/// <summary>
/// Persists the Email action's delivery intent. Success means durable intent exists; it does not
/// mean an SMTP relay or recipient accepted the message.
/// </summary>
public sealed class EmailActionOutboxService(
    IAlertDeliveryStore deliveryStore,
    TimeProvider timeProvider)
{
    private readonly IAlertDeliveryStore _deliveryStore =
        deliveryStore ?? throw new ArgumentNullException(nameof(deliveryStore));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<AlertResult<QueuedEmailActionDto>> QueueAsync(
        QueueEmailActionCommand command,
        CancellationToken cancellationToken)
    {
        if (command is null
            || command.WorkflowRunId == Guid.Empty
            || command.WorkflowStepRunId == Guid.Empty
            || string.IsNullOrWhiteSpace(command.WorkflowNodeId)
            || command.EmailDeliveryProfileId == Guid.Empty
            || command.Recipients is null
            || command.Variables is null)
        {
            return AlertResults.Validation<QueuedEmailActionDto>(
                "email.action_command_required",
                "A complete Email action command is required.");
        }

        try
        {
            var template = new EmailTemplate(command.SubjectTemplate, command.BodyTemplate);
            var message = template.Render(command.Recipients, command.Variables);
            var delivery = AlertDelivery.ForEmailAction(
                command.WorkflowRunId,
                command.WorkflowStepRunId,
                command.WorkflowNodeId,
                command.EmailDeliveryProfileId,
                message,
                _timeProvider.GetUtcNow(),
                command.MaximumAttempts);
            var write = await _deliveryStore.EnqueueEmailActionAsync(delivery, cancellationToken)
                .ConfigureAwait(false);
            return write.Status switch
            {
                AlertEnqueueStatus.Enqueued =>
                    AlertResult<QueuedEmailActionDto>.Success(new QueuedEmailActionDto(
                        write.DeliveryId ?? delivery.Id,
                        delivery.DeduplicationKey,
                        AlreadyExisted: false)),
                AlertEnqueueStatus.AlreadyEnqueued =>
                    AlertResult<QueuedEmailActionDto>.Success(new QueuedEmailActionDto(
                        write.DeliveryId ?? delivery.Id,
                        delivery.DeduplicationKey,
                        AlreadyExisted: true)),
                AlertEnqueueStatus.ReferencedResourceUnavailable =>
                    AlertResults.Conflict<QueuedEmailActionDto>(
                        "email.action_reference_changed",
                        "The run, step, or delivery profile changed before Email intent was saved."),
                _ => AlertResults.Conflict<QueuedEmailActionDto>(
                    "email.action_enqueue_conflict",
                    "The Email action could not be queued because durable state changed.")
            };
        }
        catch (Exception exception) when (AlertResults.IsDomainException(exception))
        {
            return AlertResults.DomainFailure<QueuedEmailActionDto>(exception);
        }
    }
}
