using System.Collections.ObjectModel;
using THub.Domain.Runs;

namespace THub.Domain.Alerts;

[Flags]
public enum WorkflowAlertTriggers
{
    None = 0,
    RunFailed = 1,
    RunSucceeded = 2,
    RunCancelled = 4
}

public sealed class WorkflowAlertRule
{
    private const WorkflowAlertTriggers AllTriggers =
        WorkflowAlertTriggers.RunFailed
        | WorkflowAlertTriggers.RunSucceeded
        | WorkflowAlertTriggers.RunCancelled;

    private IReadOnlyList<string> _recipients =
        Array.AsReadOnly(Array.Empty<string>());

    private WorkflowAlertRule() { }

    public WorkflowAlertRule(
        Guid workflowId,
        Guid emailDeliveryProfileId,
        string name,
        WorkflowAlertTriggers triggers,
        IEnumerable<string> recipients,
        EmailTemplate template,
        string createdBy,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(template);
        ValidateTriggers(triggers);
        var normalizedRecipients = NormalizeRecipients(recipients);

        var timestamp = DomainGuard.Utc(createdAtUtc, nameof(createdAtUtc));
        Id = Guid.NewGuid();
        WorkflowId = DomainGuard.RequireId(workflowId, nameof(workflowId));
        EmailDeliveryProfileId = DomainGuard.RequireId(
            emailDeliveryProfileId,
            nameof(emailDeliveryProfileId));
        Name = DomainGuard.Require(name, nameof(name), 200);
        Triggers = triggers;
        Template = template;
        CreatedBy = DomainGuard.Require(createdBy, nameof(createdBy), 256);
        CreatedAtUtc = timestamp;
        UpdatedAtUtc = timestamp;
        _recipients = Array.AsReadOnly(normalizedRecipients);
    }

    public Guid Id { get; private set; }

    public Guid WorkflowId { get; private set; }

    public Guid EmailDeliveryProfileId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public WorkflowAlertTriggers Triggers { get; private set; }

    public IReadOnlyList<string> Recipients => _recipients;

    public EmailTemplate Template { get; private set; } = null!;

    public bool IsEnabled { get; private set; } = true;

    public string CreatedBy { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public bool Matches(WorkflowRunStatus status)
    {
        if (!IsEnabled)
        {
            return false;
        }

        var trigger = status switch
        {
            WorkflowRunStatus.Failed => WorkflowAlertTriggers.RunFailed,
            WorkflowRunStatus.Succeeded => WorkflowAlertTriggers.RunSucceeded,
            WorkflowRunStatus.Cancelled => WorkflowAlertTriggers.RunCancelled,
            _ => WorkflowAlertTriggers.None
        };
        return trigger != WorkflowAlertTriggers.None && Triggers.HasFlag(trigger);
    }

    public void Disable(DateTimeOffset changedAtUtc) => SetEnabled(false, changedAtUtc);

    public void Enable(DateTimeOffset changedAtUtc) => SetEnabled(true, changedAtUtc);

    public void Update(
        Guid emailDeliveryProfileId,
        string name,
        WorkflowAlertTriggers triggers,
        IEnumerable<string> recipients,
        EmailTemplate template,
        DateTimeOffset changedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(template);
        ValidateTriggers(triggers);
        var normalizedRecipients = NormalizeRecipients(recipients);
        var timestamp = DomainGuard.OnOrAfter(
            changedAtUtc,
            UpdatedAtUtc,
            nameof(changedAtUtc));

        EmailDeliveryProfileId = DomainGuard.RequireId(
            emailDeliveryProfileId,
            nameof(emailDeliveryProfileId));
        Name = DomainGuard.Require(name, nameof(name), 200);
        Triggers = triggers;
        Template = template;
        _recipients = Array.AsReadOnly(normalizedRecipients);
        UpdatedAtUtc = timestamp;
    }

    private void SetEnabled(bool enabled, DateTimeOffset changedAtUtc)
    {
        var timestamp = DomainGuard.OnOrAfter(
            changedAtUtc,
            UpdatedAtUtc,
            nameof(changedAtUtc));
        IsEnabled = enabled;
        UpdatedAtUtc = timestamp;
    }

    private static void ValidateTriggers(WorkflowAlertTriggers triggers)
    {
        if (triggers == WorkflowAlertTriggers.None || (triggers & ~AllTriggers) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(triggers),
                "At least one supported terminal run event is required.");
        }
    }

    private static string[] NormalizeRecipients(IEnumerable<string> recipients)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        var normalizedRecipients = recipients
            .Select(recipient => EmailPolicy.Address(recipient, nameof(recipients)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedRecipients.Length == 0
            || normalizedRecipients.Length > EmailDeliveryLimits.AbsoluteMaximumRecipients)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recipients),
                $"Rules require between 1 and {EmailDeliveryLimits.AbsoluteMaximumRecipients} recipients.");
        }

        return normalizedRecipients;
    }
}
