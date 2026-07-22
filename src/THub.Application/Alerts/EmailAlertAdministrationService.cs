using THub.Domain.Alerts;
using THub.Domain.Runs;

namespace THub.Application.Alerts;

public sealed class EmailAlertAdministrationService(
    IEmailAlertAdministrationStore store,
    TimeProvider timeProvider)
{
    private static readonly IReadOnlyDictionary<string, string?> MaximumTemplateVariables =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["workflow.id"] = Guid.Empty.ToString("D"),
            ["workflow.name"] = new string('W', 200),
            ["run.id"] = Guid.Empty.ToString("D"),
            ["run.status"] = WorkflowRunStatus.Cancelled.ToString(),
            ["run.triggeredBy"] = new string('A', 256),
            ["run.startedAtUtc"] = DateTimeOffset.MaxValue.ToString("O"),
            ["run.completedAtUtc"] = DateTimeOffset.MaxValue.ToString("O"),
            ["error.code"] = new string('c', ExecutionError.MaximumCodeLength),
            ["error.category"] = ExecutionErrorCategory.ExternalSideEffect.ToString(),
            ["error.summary"] = new string('e', ExecutionError.MaximumSummaryLength)
        };

    private readonly IEmailAlertAdministrationStore _store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<AlertResult<IReadOnlyList<EmailDeliveryProfileDto>>> ListProfilesAsync(
        CancellationToken cancellationToken)
    {
        var profiles = await _store.ListProfilesAsync(cancellationToken).ConfigureAwait(false);
        return AlertResult<IReadOnlyList<EmailDeliveryProfileDto>>.Success(
            profiles.Select(ToDto).ToArray());
    }

    public async Task<AlertResult<EmailDeliveryProfileDto>> GetProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken)
    {
        if (profileId == Guid.Empty)
        {
            return AlertResults.Validation<EmailDeliveryProfileDto>(
                "email.profile_id_required",
                "An Email delivery profile identifier is required.");
        }

        var profile = await _store.FindProfileAsync(profileId, cancellationToken)
            .ConfigureAwait(false);
        return profile is null
            ? AlertResults.NotFound<EmailDeliveryProfileDto>(
                "email.profile_not_found",
                "The Email delivery profile was not found.")
            : AlertResult<EmailDeliveryProfileDto>.Success(ToDto(profile));
    }

    public async Task<AlertResult<EmailDeliveryProfileDto>> CreateProfileAsync(
        CreateEmailDeliveryProfileCommand command,
        CancellationToken cancellationToken)
    {
        if (command is null || command.AllowedRecipientDomains is null)
        {
            return AlertResults.Validation<EmailDeliveryProfileDto>(
                "email.profile_command_required",
                "A complete Email delivery profile command is required.");
        }

        try
        {
            var profile = new EmailDeliveryProfile(
                command.Name,
                command.SmtpHost,
                command.SmtpPort,
                command.TransportSecurity,
                command.SenderAddress,
                command.AllowedRecipientDomains,
                command.CreatedBy,
                _timeProvider.GetUtcNow(),
                command.CredentialSecretReference,
                command.Limits);
            var status = await _store.AddProfileAsync(profile, cancellationToken)
                .ConfigureAwait(false);
            return status switch
            {
                EmailAlertAdministrationWriteStatus.Saved =>
                    AlertResult<EmailDeliveryProfileDto>.Success(ToDto(profile)),
                EmailAlertAdministrationWriteStatus.DuplicateName =>
                    AlertResults.Conflict<EmailDeliveryProfileDto>(
                        "email.profile_name_exists",
                        "An Email delivery profile with that name already exists."),
                _ => AlertResults.Conflict<EmailDeliveryProfileDto>(
                    "email.profile_create_conflict",
                    "The Email delivery profile could not be created because state changed.")
            };
        }
        catch (Exception exception) when (AlertResults.IsDomainException(exception))
        {
            return AlertResults.DomainFailure<EmailDeliveryProfileDto>(exception);
        }
    }

    public async Task<AlertResult<EmailDeliveryProfileDto>> SetProfileEnabledAsync(
        SetEmailDeliveryProfileEnabledCommand command,
        CancellationToken cancellationToken)
    {
        if (command is null || command.ProfileId == Guid.Empty)
        {
            return AlertResults.Validation<EmailDeliveryProfileDto>(
                "email.profile_command_required",
                "A profile identifier and expected revision are required.");
        }

        var profile = await _store.FindProfileAsync(command.ProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            return AlertResults.NotFound<EmailDeliveryProfileDto>(
                "email.profile_not_found",
                "The Email delivery profile was not found.");
        }

        if (profile.UpdatedAtUtc != command.ExpectedUpdatedAtUtc.ToUniversalTime())
        {
            return AlertResults.Conflict<EmailDeliveryProfileDto>(
                "email.profile_concurrency_conflict",
                "The Email delivery profile changed. Reload it before trying again.");
        }

        try
        {
            if (command.IsEnabled)
            {
                var rules = await _store.ListRulesForProfileAsync(
                        profile.Id,
                        cancellationToken)
                    .ConfigureAwait(false);
                foreach (var rule in rules.Where(rule => rule.IsEnabled))
                {
                    ValidateRuleAgainstProfile(rule, profile);
                }

                profile.Enable(_timeProvider.GetUtcNow());
            }
            else
            {
                profile.Disable(_timeProvider.GetUtcNow());
            }

            var status = await _store.SaveProfileAsync(
                    profile,
                    command.ExpectedUpdatedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            return MapProfileWrite(status, profile);
        }
        catch (Exception exception) when (AlertResults.IsDomainException(exception))
        {
            return AlertResults.DomainFailure<EmailDeliveryProfileDto>(exception);
        }
    }

    public async Task<AlertResult<EmailDeliveryProfileDto>> UpdateProfileAsync(
        UpdateEmailDeliveryProfileCommand command,
        CancellationToken cancellationToken)
    {
        if (command is null || command.ProfileId == Guid.Empty
            || command.AllowedRecipientDomains is null || command.Limits is null)
        {
            return AlertResults.Validation<EmailDeliveryProfileDto>(
                "email.profile_command_required",
                "A complete profile update and expected revision are required.");
        }

        var profile = await _store.FindProfileAsync(command.ProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            return AlertResults.NotFound<EmailDeliveryProfileDto>(
                "email.profile_not_found",
                "The Email delivery profile was not found.");
        }

        if (profile.UpdatedAtUtc != command.ExpectedUpdatedAtUtc.ToUniversalTime())
        {
            return AlertResults.Conflict<EmailDeliveryProfileDto>(
                "email.profile_concurrency_conflict",
                "The Email delivery profile changed. Reload it before trying again.");
        }

        try
        {
            profile.Update(
                command.Name,
                command.SmtpHost,
                command.SmtpPort,
                command.TransportSecurity,
                command.SenderAddress,
                command.AllowedRecipientDomains,
                command.CredentialSecretReference,
                command.Limits,
                _timeProvider.GetUtcNow());
            var rules = await _store.ListRulesForProfileAsync(profile.Id, cancellationToken)
                .ConfigureAwait(false);
            foreach (var rule in rules.Where(rule => rule.IsEnabled))
            {
                ValidateRuleAgainstProfile(rule, profile);
            }

            var status = await _store.SaveProfileAsync(
                    profile,
                    command.ExpectedUpdatedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            return MapProfileWrite(status, profile);
        }
        catch (Exception exception) when (AlertResults.IsDomainException(exception))
        {
            return AlertResults.DomainFailure<EmailDeliveryProfileDto>(exception);
        }
    }

    public async Task<AlertResult<IReadOnlyList<WorkflowAlertRuleDto>>> ListRulesAsync(
        Guid workflowId,
        CancellationToken cancellationToken)
    {
        if (workflowId == Guid.Empty)
        {
            return AlertResults.Validation<IReadOnlyList<WorkflowAlertRuleDto>>(
                "email.workflow_id_required",
                "A workflow identifier is required.");
        }

        if (!await _store.WorkflowExistsAsync(workflowId, cancellationToken).ConfigureAwait(false))
        {
            return AlertResults.NotFound<IReadOnlyList<WorkflowAlertRuleDto>>(
                "email.workflow_not_found",
                "The workflow was not found.");
        }

        var rules = await _store.ListRulesAsync(workflowId, cancellationToken)
            .ConfigureAwait(false);
        return AlertResult<IReadOnlyList<WorkflowAlertRuleDto>>.Success(
            rules.Select(ToDto).ToArray());
    }

    public async Task<AlertResult<WorkflowAlertRuleDto>> CreateRuleAsync(
        CreateWorkflowAlertRuleCommand command,
        CancellationToken cancellationToken)
    {
        if (command is null || command.WorkflowId == Guid.Empty
            || command.EmailDeliveryProfileId == Guid.Empty
            || command.Recipients is null)
        {
            return AlertResults.Validation<WorkflowAlertRuleDto>(
                "email.rule_command_required",
                "A complete workflow alert rule command is required.");
        }

        if (!await _store.WorkflowExistsAsync(command.WorkflowId, cancellationToken)
                .ConfigureAwait(false))
        {
            return AlertResults.NotFound<WorkflowAlertRuleDto>(
                "email.workflow_not_found",
                "The workflow was not found.");
        }

        var profile = await _store.FindProfileAsync(
                command.EmailDeliveryProfileId,
                cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            return AlertResults.NotFound<WorkflowAlertRuleDto>(
                "email.profile_not_found",
                "The Email delivery profile was not found.");
        }

        if (!profile.IsEnabled)
        {
            return AlertResults.Conflict<WorkflowAlertRuleDto>(
                "email.profile_disabled",
                "Enable the Email delivery profile before creating an active rule.");
        }

        try
        {
            var rule = new WorkflowAlertRule(
                command.WorkflowId,
                command.EmailDeliveryProfileId,
                command.Name,
                command.Triggers,
                command.Recipients,
                new EmailTemplate(command.SubjectTemplate, command.BodyTemplate),
                command.CreatedBy,
                _timeProvider.GetUtcNow());
            ValidateRuleAgainstProfile(rule, profile);

            var status = await _store.AddRuleAsync(rule, cancellationToken).ConfigureAwait(false);
            return status switch
            {
                EmailAlertAdministrationWriteStatus.Saved =>
                    AlertResult<WorkflowAlertRuleDto>.Success(ToDto(rule)),
                EmailAlertAdministrationWriteStatus.DuplicateName =>
                    AlertResults.Conflict<WorkflowAlertRuleDto>(
                        "email.rule_name_exists",
                        "This workflow already has an alert rule with that name."),
                EmailAlertAdministrationWriteStatus.ReferencedResourceUnavailable =>
                    AlertResults.Conflict<WorkflowAlertRuleDto>(
                        "email.rule_reference_changed",
                        "The workflow or delivery profile changed while the rule was saved."),
                _ => AlertResults.Conflict<WorkflowAlertRuleDto>(
                    "email.rule_create_conflict",
                    "The workflow alert rule could not be created because state changed.")
            };
        }
        catch (Exception exception) when (AlertResults.IsDomainException(exception))
        {
            return AlertResults.DomainFailure<WorkflowAlertRuleDto>(exception);
        }
    }

    public async Task<AlertResult<WorkflowAlertRuleDto>> SetRuleEnabledAsync(
        SetWorkflowAlertRuleEnabledCommand command,
        CancellationToken cancellationToken)
    {
        if (command is null || command.RuleId == Guid.Empty)
        {
            return AlertResults.Validation<WorkflowAlertRuleDto>(
                "email.rule_command_required",
                "A rule identifier and expected revision are required.");
        }

        var rule = await _store.FindRuleAsync(command.RuleId, cancellationToken)
            .ConfigureAwait(false);
        if (rule is null)
        {
            return AlertResults.NotFound<WorkflowAlertRuleDto>(
                "email.rule_not_found",
                "The workflow alert rule was not found.");
        }

        if (rule.UpdatedAtUtc != command.ExpectedUpdatedAtUtc.ToUniversalTime())
        {
            return AlertResults.Conflict<WorkflowAlertRuleDto>(
                "email.rule_concurrency_conflict",
                "The workflow alert rule changed. Reload it before trying again.");
        }

        try
        {
            if (command.IsEnabled)
            {
                var profile = await _store.FindProfileAsync(
                        rule.EmailDeliveryProfileId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (profile is null || !profile.IsEnabled)
                {
                    return AlertResults.Conflict<WorkflowAlertRuleDto>(
                        "email.profile_unavailable",
                        "The rule cannot be enabled until its delivery profile is enabled.");
                }

                ValidateRuleAgainstProfile(rule, profile);
                rule.Enable(_timeProvider.GetUtcNow());
            }
            else
            {
                rule.Disable(_timeProvider.GetUtcNow());
            }

            var status = await _store.SaveRuleAsync(
                    rule,
                    command.ExpectedUpdatedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            return status switch
            {
                EmailAlertAdministrationWriteStatus.Saved =>
                    AlertResult<WorkflowAlertRuleDto>.Success(ToDto(rule)),
                EmailAlertAdministrationWriteStatus.NotFound =>
                    AlertResults.NotFound<WorkflowAlertRuleDto>(
                        "email.rule_not_found",
                        "The workflow alert rule was not found."),
                _ => AlertResults.Conflict<WorkflowAlertRuleDto>(
                    "email.rule_concurrency_conflict",
                    "The workflow alert rule changed. Reload it before trying again.")
            };
        }
        catch (Exception exception) when (AlertResults.IsDomainException(exception))
        {
            return AlertResults.DomainFailure<WorkflowAlertRuleDto>(exception);
        }
    }

    public async Task<AlertResult<WorkflowAlertRuleDto>> UpdateRuleAsync(
        UpdateWorkflowAlertRuleCommand command,
        CancellationToken cancellationToken)
    {
        if (command is null || command.RuleId == Guid.Empty
            || command.EmailDeliveryProfileId == Guid.Empty
            || command.Recipients is null)
        {
            return AlertResults.Validation<WorkflowAlertRuleDto>(
                "email.rule_command_required",
                "A complete rule update and expected revision are required.");
        }

        var rule = await _store.FindRuleAsync(command.RuleId, cancellationToken)
            .ConfigureAwait(false);
        if (rule is null)
        {
            return AlertResults.NotFound<WorkflowAlertRuleDto>(
                "email.rule_not_found",
                "The workflow alert rule was not found.");
        }

        if (rule.UpdatedAtUtc != command.ExpectedUpdatedAtUtc.ToUniversalTime())
        {
            return AlertResults.Conflict<WorkflowAlertRuleDto>(
                "email.rule_concurrency_conflict",
                "The workflow alert rule changed. Reload it before trying again.");
        }

        var profile = await _store.FindProfileAsync(
                command.EmailDeliveryProfileId,
                cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            return AlertResults.NotFound<WorkflowAlertRuleDto>(
                "email.profile_not_found",
                "The Email delivery profile was not found.");
        }

        if (rule.IsEnabled && !profile.IsEnabled)
        {
            return AlertResults.Conflict<WorkflowAlertRuleDto>(
                "email.profile_disabled",
                "An enabled alert rule requires an enabled delivery profile.");
        }

        try
        {
            rule.Update(
                command.EmailDeliveryProfileId,
                command.Name,
                command.Triggers,
                command.Recipients,
                new EmailTemplate(command.SubjectTemplate, command.BodyTemplate),
                _timeProvider.GetUtcNow());
            ValidateRuleAgainstProfile(rule, profile);
            var status = await _store.SaveRuleAsync(
                    rule,
                    command.ExpectedUpdatedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            return status switch
            {
                EmailAlertAdministrationWriteStatus.Saved =>
                    AlertResult<WorkflowAlertRuleDto>.Success(ToDto(rule)),
                EmailAlertAdministrationWriteStatus.NotFound =>
                    AlertResults.NotFound<WorkflowAlertRuleDto>(
                        "email.rule_not_found",
                        "The workflow alert rule was not found."),
                EmailAlertAdministrationWriteStatus.DuplicateName =>
                    AlertResults.Conflict<WorkflowAlertRuleDto>(
                        "email.rule_name_exists",
                        "This workflow already has an alert rule with that name."),
                EmailAlertAdministrationWriteStatus.ReferencedResourceUnavailable =>
                    AlertResults.Conflict<WorkflowAlertRuleDto>(
                        "email.rule_reference_changed",
                        "The delivery profile changed while the rule was saved."),
                _ => AlertResults.Conflict<WorkflowAlertRuleDto>(
                    "email.rule_concurrency_conflict",
                    "The workflow alert rule changed. Reload it before trying again.")
            };
        }
        catch (Exception exception) when (AlertResults.IsDomainException(exception))
        {
            return AlertResults.DomainFailure<WorkflowAlertRuleDto>(exception);
        }
    }

    private static void ValidateRuleAgainstProfile(
        WorkflowAlertRule rule,
        EmailDeliveryProfile profile)
    {
        var maximumMessage = rule.Template.Render(rule.Recipients, MaximumTemplateVariables);
        profile.ValidateMessagePolicy(maximumMessage);
    }

    private static AlertResult<EmailDeliveryProfileDto> MapProfileWrite(
        EmailAlertAdministrationWriteStatus status,
        EmailDeliveryProfile profile) => status switch
        {
            EmailAlertAdministrationWriteStatus.Saved =>
                AlertResult<EmailDeliveryProfileDto>.Success(ToDto(profile)),
            EmailAlertAdministrationWriteStatus.NotFound =>
                AlertResults.NotFound<EmailDeliveryProfileDto>(
                    "email.profile_not_found",
                    "The Email delivery profile was not found."),
            EmailAlertAdministrationWriteStatus.DuplicateName =>
                AlertResults.Conflict<EmailDeliveryProfileDto>(
                    "email.profile_name_exists",
                    "An Email delivery profile with that name already exists."),
            _ => AlertResults.Conflict<EmailDeliveryProfileDto>(
                "email.profile_concurrency_conflict",
                "The Email delivery profile changed. Reload it before trying again.")
        };

    private static EmailDeliveryProfileDto ToDto(EmailDeliveryProfile profile) => new(
        profile.Id,
        profile.Name,
        profile.SmtpHost,
        profile.SmtpPort,
        profile.TransportSecurity,
        profile.SenderAddress,
        profile.AllowedRecipientDomains.ToArray(),
        profile.CredentialSecretReference,
        profile.Limits,
        profile.IsEnabled,
        profile.CreatedBy,
        profile.CreatedAtUtc,
        profile.UpdatedAtUtc);

    private static WorkflowAlertRuleDto ToDto(WorkflowAlertRule rule) => new(
        rule.Id,
        rule.WorkflowId,
        rule.EmailDeliveryProfileId,
        rule.Name,
        rule.Triggers,
        rule.Recipients.ToArray(),
        rule.Template.Subject,
        rule.Template.Body,
        rule.IsEnabled,
        rule.CreatedBy,
        rule.CreatedAtUtc,
        rule.UpdatedAtUtc);
}
