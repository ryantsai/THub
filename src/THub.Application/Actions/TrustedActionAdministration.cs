using THub.Application.Connections;
using THub.Application.Execution;
using THub.Application.Workflows;
using THub.Domain.Actions;
using THub.Domain.Security;
using THub.Domain.Workflows;

namespace THub.Application.Actions;

public static class TrustedActionCredentialReferences
{
    private const string StoragePrefix = "trusted-action.";

    public static string ToStorageReference(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        return StoragePrefix + reference.Trim();
    }
}

public sealed record TrustedActionSummary(
    Guid Id,
    string Name,
    TrustedActionKind Kind,
    bool HasStoredCredential,
    bool IsEnabled,
    DateTimeOffset UpdatedAtUtc);

public sealed record TrustedActionDetails(
    Guid Id,
    string Name,
    TrustedActionKind Kind,
    TrustedActionDefinition Definition,
    string? CredentialReference,
    bool HasStoredCredential,
    bool IsEnabled,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record SaveTrustedActionCommand(
    Guid? Id,
    string Name,
    TrustedActionKind Kind,
    TrustedActionDefinition Definition,
    string? CredentialReference,
    string? CredentialUserName,
    string? CredentialPassword,
    DateTimeOffset? ExpectedUpdatedAtUtc,
    string Actor);

public enum TrustedActionWriteStatus
{
    Saved,
    NotFound,
    Conflict,
    DuplicateName,
}

public sealed record TrustedActionWriteResult(
    TrustedActionWriteStatus Status,
    TrustedActionDetails? Action = null,
    string? Code = null,
    string? Message = null);

public interface ITrustedActionStore
{
    Task<IReadOnlyList<TrustedAction>> ListAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<TrustedAction>> ListAvailableAsync(
        IReadOnlySet<Guid> roleIds,
        CancellationToken cancellationToken);

    Task<TrustedAction?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> CredentialExistsAsync(
        string credentialReference,
        CancellationToken cancellationToken);

    Task<TrustedActionWriteStatus> AddAsync(
        TrustedAction action,
        ConnectionCredentialWrite? credentialWrite,
        CancellationToken cancellationToken);

    Task<TrustedActionWriteStatus> SaveAsync(
        TrustedAction action,
        DateTimeOffset expectedUpdatedAtUtc,
        ConnectionCredentialWrite? credentialWrite,
        CancellationToken cancellationToken);
}

public sealed class TrustedActionAdministrationService(
    ITrustedActionStore store,
    TrustedActionDefinitionSerializer serializer,
    TimeProvider timeProvider)
{
    private readonly ITrustedActionStore _store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly TrustedActionDefinitionSerializer _serializer =
        serializer ?? throw new ArgumentNullException(nameof(serializer));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<IReadOnlyList<TrustedActionSummary>> ListAsync(
        CancellationToken cancellationToken)
    {
        var actions = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<TrustedActionSummary>(actions.Count);
        foreach (var action in actions)
        {
            result.Add(new(
                action.Id,
                action.Name,
                action.Kind,
                action.CredentialReference is not null &&
                    await _store.CredentialExistsAsync(
                        action.CredentialReference,
                        cancellationToken).ConfigureAwait(false),
                action.IsEnabled,
                action.UpdatedAtUtc));
        }

        return result;
    }

    public async Task<IReadOnlyList<TrustedActionSummary>> ListAvailableAsync(
        IReadOnlySet<Guid> roleIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roleIds);
        var actions = await _store.ListAvailableAsync(roleIds, cancellationToken)
            .ConfigureAwait(false);
        return actions
            .Where(action => action.IsEnabled)
            .Select(action => new TrustedActionSummary(
                action.Id,
                action.Name,
                action.Kind,
                action.CredentialReference is not null,
                action.IsEnabled,
                action.UpdatedAtUtc))
            .ToArray();
    }

    public async Task<TrustedActionDetails?> GetAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A trusted action identifier is required.", nameof(id));
        }

        var action = await _store.FindAsync(id, cancellationToken).ConfigureAwait(false);
        return action is null ? null : await ToDetailsAsync(action, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TrustedActionWriteResult> SaveAsync(
        SaveTrustedActionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Id == Guid.Empty ||
            command.Definition is null ||
            command.Definition.Kind != command.Kind ||
            string.IsNullOrWhiteSpace(command.Actor) ||
            (command.Id is null) != (command.ExpectedUpdatedAtUtc is null))
        {
            throw new ArgumentException("The trusted action command is invalid.", nameof(command));
        }

        var now = _timeProvider.GetUtcNow();
        var definitionJson = _serializer.Serialize(command.Definition);
        _ = _serializer.Deserialize(command.Kind, definitionJson);
        var credentialWrite = CreateCredentialWrite(command, now);

        if (command.Id is null)
        {
            var action = new TrustedAction(
                Guid.NewGuid(),
                command.Name,
                command.Kind,
                definitionJson,
                command.CredentialReference,
                command.Actor,
                now);
            if (RequiresCredential(action, command.Definition) &&
                credentialWrite is null)
            {
                return CredentialRequired();
            }

            var status = await _store.AddAsync(
                action,
                credentialWrite,
                cancellationToken).ConfigureAwait(false);
            return await MapWriteAsync(status, action, cancellationToken).ConfigureAwait(false);
        }

        var existing = await _store.FindAsync(command.Id.Value, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return new(TrustedActionWriteStatus.NotFound);
        }

        if (existing.Kind != command.Kind ||
            existing.UpdatedAtUtc != command.ExpectedUpdatedAtUtc!.Value.ToUniversalTime())
        {
            return new(
                TrustedActionWriteStatus.Conflict,
                await ToDetailsAsync(existing, cancellationToken).ConfigureAwait(false),
                "trusted-action.concurrency",
                "The trusted action changed after it was loaded.");
        }

        existing.Update(
            command.Name,
            definitionJson,
            command.CredentialReference,
            command.Actor,
            now);
        if (RequiresCredential(existing, command.Definition) &&
            credentialWrite is null &&
            (existing.CredentialReference is null ||
             !await _store.CredentialExistsAsync(
                 existing.CredentialReference,
                 cancellationToken).ConfigureAwait(false)))
        {
            return CredentialRequired();
        }

        var writeStatus = await _store.SaveAsync(
            existing,
            command.ExpectedUpdatedAtUtc.Value,
            credentialWrite,
            cancellationToken).ConfigureAwait(false);
        return await MapWriteAsync(writeStatus, existing, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TrustedActionWriteResult> SetEnabledAsync(
        Guid id,
        bool enabled,
        string actor,
        DateTimeOffset expectedUpdatedAtUtc,
        CancellationToken cancellationToken)
    {
        var action = await _store.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (action is null)
        {
            return new(TrustedActionWriteStatus.NotFound);
        }

        if (action.UpdatedAtUtc != expectedUpdatedAtUtc.ToUniversalTime())
        {
            return new(
                TrustedActionWriteStatus.Conflict,
                await ToDetailsAsync(action, cancellationToken).ConfigureAwait(false),
                "trusted-action.concurrency");
        }

        action.SetEnabled(enabled, actor, _timeProvider.GetUtcNow());
        var status = await _store.SaveAsync(
            action,
            expectedUpdatedAtUtc,
            credentialWrite: null,
            cancellationToken).ConfigureAwait(false);
        return await MapWriteAsync(status, action, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TrustedActionWriteResult> MapWriteAsync(
        TrustedActionWriteStatus status,
        TrustedAction action,
        CancellationToken cancellationToken) => status switch
        {
            TrustedActionWriteStatus.Saved => new(
                status,
                await ToDetailsAsync(action, cancellationToken).ConfigureAwait(false)),
            TrustedActionWriteStatus.DuplicateName => new(
                status,
                null,
                "trusted-action.name.duplicate",
                "A trusted action with this name already exists."),
            _ => new(status),
        };

    private async Task<TrustedActionDetails> ToDetailsAsync(
        TrustedAction action,
        CancellationToken cancellationToken) => new(
            action.Id,
            action.Name,
            action.Kind,
            _serializer.Deserialize(action),
            action.CredentialReference,
            action.CredentialReference is not null &&
                await _store.CredentialExistsAsync(
                    action.CredentialReference,
                    cancellationToken).ConfigureAwait(false),
            action.IsEnabled,
            action.CreatedBy,
            action.CreatedAtUtc,
            action.UpdatedAtUtc);

    private static bool RequiresCredential(
        TrustedAction action,
        TrustedActionDefinition definition) =>
        definition is WebhookActionDefinition
        {
            Authentication: not WebhookAuthenticationKind.None,
        } ||
        definition is ExecutableActionDefinition && action.CredentialReference is not null;

    private static ConnectionCredentialWrite? CreateCredentialWrite(
        SaveTrustedActionCommand command,
        DateTimeOffset changedAtUtc)
    {
        var hasUserName = !string.IsNullOrWhiteSpace(command.CredentialUserName);
        var hasPassword = !string.IsNullOrEmpty(command.CredentialPassword);
        if (hasUserName != hasPassword)
        {
            throw new ArgumentException(
                "Enter both a credential username and password, or leave both empty.",
                nameof(command));
        }

        if (!hasUserName)
        {
            return null;
        }

        if (command.CredentialUserName!.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Credential usernames cannot contain control characters.",
                nameof(command));
        }

        if (string.IsNullOrWhiteSpace(command.CredentialReference))
        {
            throw new ArgumentException(
                "A credential reference is required when replacing a credential.",
                nameof(command));
        }

        return new ConnectionCredentialWrite(
            TrustedActionCredentialReferences.ToStorageReference(
                command.CredentialReference),
            new ConnectionCredential(
                command.CredentialUserName.Trim(),
                command.CredentialPassword!),
            changedAtUtc);
    }

    private static TrustedActionWriteResult CredentialRequired() => new(
        TrustedActionWriteStatus.Conflict,
        null,
        "trusted-action.credential.required",
        "Enter a credential reference, username, and password for this trusted action.");
}

public sealed class TrustedActionWorkflowAuthorization(ITrustedActionStore store)
{
    public async Task<IReadOnlyList<GraphValidationIssue>> ValidatePublishAsync(
        WorkflowGraph graph,
        IReadOnlySet<Guid> roleIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(roleIds);
        var referencedNodes = graph.Nodes
            .Where(node => node.Kind is WorkflowNodeKind.Webhook or WorkflowNodeKind.Executable)
            .ToArray();
        if (referencedNodes.Length == 0)
        {
            return [];
        }

        var available = await store.ListAvailableAsync(roleIds, cancellationToken)
            .ConfigureAwait(false);
        var byId = available.ToDictionary(action => action.Id);
        var issues = new List<GraphValidationIssue>();
        var settingsValidator = new WorkflowNodeSettingsValidator();
        foreach (var node in referencedNodes)
        {
            WorkflowNodeSettings settings;
            try
            {
                settings = settingsValidator.Parse(node);
            }
            catch (WorkflowNodeSettingsException)
            {
                continue;
            }

            var trustedActionId = settings switch
            {
                WebhookNodeSettings webhook => webhook.TrustedActionId,
                ExecutableNodeSettings executable => executable.TrustedActionId,
                _ => Guid.Empty,
            };
            var expectedKind = node.Kind == WorkflowNodeKind.Webhook
                ? TrustedActionKind.Webhook
                : TrustedActionKind.Executable;
            if (!byId.TryGetValue(trustedActionId, out var action) ||
                !action.IsEnabled ||
                action.Kind != expectedKind)
            {
                issues.Add(new(
                    "node.trusted-action.unauthorized",
                    "The publisher is not authorized to use the selected enabled trusted action.",
                    node.Id));
            }
        }

        return issues;
    }
}
