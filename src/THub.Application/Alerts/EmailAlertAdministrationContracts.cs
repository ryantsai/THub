using THub.Domain.Alerts;

namespace THub.Application.Alerts;

public sealed record EmailDeliveryProfileDto(
    Guid Id,
    string Name,
    string SmtpHost,
    int SmtpPort,
    EmailTransportSecurity TransportSecurity,
    string SenderAddress,
    IReadOnlyList<string> AllowedRecipientDomains,
    string? CredentialSecretReference,
    EmailDeliveryLimits Limits,
    bool IsEnabled,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record WorkflowAlertRuleDto(
    Guid Id,
    Guid WorkflowId,
    Guid EmailDeliveryProfileId,
    string Name,
    WorkflowAlertTriggers Triggers,
    IReadOnlyList<string> Recipients,
    string SubjectTemplate,
    string BodyTemplate,
    bool IsEnabled,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record CreateEmailDeliveryProfileCommand(
    string Name,
    string SmtpHost,
    int SmtpPort,
    EmailTransportSecurity TransportSecurity,
    string SenderAddress,
    IReadOnlyList<string> AllowedRecipientDomains,
    string CreatedBy,
    string? CredentialSecretReference = null,
    EmailDeliveryLimits? Limits = null);

public sealed record SetEmailDeliveryProfileEnabledCommand(
    Guid ProfileId,
    bool IsEnabled,
    DateTimeOffset ExpectedUpdatedAtUtc);

public sealed record UpdateEmailDeliveryProfileCommand(
    Guid ProfileId,
    string Name,
    string SmtpHost,
    int SmtpPort,
    EmailTransportSecurity TransportSecurity,
    string SenderAddress,
    IReadOnlyList<string> AllowedRecipientDomains,
    string? CredentialSecretReference,
    EmailDeliveryLimits Limits,
    DateTimeOffset ExpectedUpdatedAtUtc);

public sealed record CreateWorkflowAlertRuleCommand(
    Guid WorkflowId,
    Guid EmailDeliveryProfileId,
    string Name,
    WorkflowAlertTriggers Triggers,
    IReadOnlyList<string> Recipients,
    string SubjectTemplate,
    string BodyTemplate,
    string CreatedBy);

public sealed record SetWorkflowAlertRuleEnabledCommand(
    Guid RuleId,
    bool IsEnabled,
    DateTimeOffset ExpectedUpdatedAtUtc);

public sealed record UpdateWorkflowAlertRuleCommand(
    Guid RuleId,
    Guid EmailDeliveryProfileId,
    string Name,
    WorkflowAlertTriggers Triggers,
    IReadOnlyList<string> Recipients,
    string SubjectTemplate,
    string BodyTemplate,
    DateTimeOffset ExpectedUpdatedAtUtc);

public enum EmailAlertAdministrationWriteStatus
{
    Saved,
    NotFound,
    Conflict,
    DuplicateName,
    ReferencedResourceUnavailable
}

public interface IEmailAlertAdministrationStore
{
    Task<IReadOnlyList<EmailDeliveryProfile>> ListProfilesAsync(
        CancellationToken cancellationToken);

    Task<EmailDeliveryProfile?> FindProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken);

    Task<EmailAlertAdministrationWriteStatus> AddProfileAsync(
        EmailDeliveryProfile profile,
        CancellationToken cancellationToken);

    Task<EmailAlertAdministrationWriteStatus> SaveProfileAsync(
        EmailDeliveryProfile profile,
        DateTimeOffset expectedUpdatedAtUtc,
        CancellationToken cancellationToken);

    Task<bool> WorkflowExistsAsync(
        Guid workflowId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkflowAlertRule>> ListRulesAsync(
        Guid workflowId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkflowAlertRule>> ListRulesForProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken);

    Task<WorkflowAlertRule?> FindRuleAsync(
        Guid ruleId,
        CancellationToken cancellationToken);

    Task<EmailAlertAdministrationWriteStatus> AddRuleAsync(
        WorkflowAlertRule rule,
        CancellationToken cancellationToken);

    Task<EmailAlertAdministrationWriteStatus> SaveRuleAsync(
        WorkflowAlertRule rule,
        DateTimeOffset expectedUpdatedAtUtc,
        CancellationToken cancellationToken);
}
