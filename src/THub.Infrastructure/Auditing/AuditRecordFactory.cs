using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using THub.Application.Auditing;
using THub.Domain.Auditing;
using THub.Domain.Runs;

namespace THub.Infrastructure.Auditing;

internal static class AuditRecordFactory
{
    private static readonly string[] ActorProperties =
    [
        "LeaseOwner",
        "UpdatedBy",
        "ReviewedBy",
        "RevokedBy",
        "CancellationRequestedBy",
        "ApplyStartedBy",
        "PublishedBy",
        "SubmittedBy",
        "TriggeredBy",
        "CreatedBy",
        "Owner",
    ];

    private static readonly string[] CorrelationProperties =
    [
        "WorkflowRunId",
        "WorkflowId",
        "PublicationId",
        "PublicationVersionId",
        "ChangeSetId",
        "RoleId",
        "ConnectionId",
    ];

    public static IReadOnlyList<AuditRecord> Create(
        ChangeTracker changeTracker,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(changeTracker);
        var source = NormalizeSource(
            Assembly.GetEntryAssembly()?.GetName().Name ?? "thub");
        return changeTracker
            .Entries()
            .Where(IsAuditable)
            .Select(entry => CreateRecord(entry, source, occurredAtUtc))
            .ToArray();
    }

    private static bool IsAuditable(EntityEntry entry)
    {
        if (entry.Entity is AuditRecord ||
            entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted) ||
            entry.Metadata.IsOwned())
        {
            return false;
        }

        if (entry.State != EntityState.Modified)
        {
            return true;
        }

        var changed = entry.Properties
            .Where(property => property.IsModified)
            .Select(property => property.Metadata.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (entry.Entity is WorkflowRun &&
            changed.All(name => name is "LastHeartbeatAtUtc" or "LeaseExpiresAtUtc"))
        {
            return false;
        }

        if (entry.Entity is WorkflowStepRun &&
            changed.All(name => name is
                "RowsRead" or
                "RowsWritten" or
                "BatchesProcessed" or
                "BytesRead" or
                "BytesWritten"))
        {
            return false;
        }

        return changed.Count > 0;
    }

    private static AuditRecord CreateRecord(
        EntityEntry entry,
        string source,
        DateTimeOffset occurredAtUtc)
    {
        var resourceType = ResourceType(entry);
        var actor = AuditContext.Current ?? InferActor(entry, source);
        return new AuditRecord(
            Guid.NewGuid(),
            occurredAtUtc,
            actor.Kind,
            actor.Identifier,
            source,
            Action(entry, resourceType),
            Outcome(entry),
            resourceType,
            ResourceIdentifier(entry),
            CorrelationIdentifier(entry));
    }

    private static AuditActorContext InferActor(EntityEntry entry, string source)
    {
        foreach (var name in ActorProperties)
        {
            var property = entry.Properties.FirstOrDefault(candidate =>
                string.Equals(candidate.Metadata.Name, name, StringComparison.Ordinal));
            var value = entry.State == EntityState.Deleted
                ? property?.OriginalValue as string
                : property?.CurrentValue as string;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return new AuditActorContext(
                    source.Equals("thub.web", StringComparison.Ordinal)
                        ? AuditActorKind.User
                        : AuditActorKind.System,
                    value.Trim());
            }
        }

        return new AuditActorContext(AuditActorKind.System, source);
    }

    private static string Action(EntityEntry entry, string resourceType)
    {
        if (entry.State == EntityState.Added)
        {
            return $"{resourceType}.created";
        }

        if (entry.State == EntityState.Deleted)
        {
            return $"{resourceType}.deleted";
        }

        var status = entry.Properties.FirstOrDefault(property =>
            property.IsModified &&
            property.Metadata.Name is "Status" or "State");
        if (status?.CurrentValue is not null)
        {
            return $"{resourceType}.{MachineName(status.CurrentValue.ToString()!)}";
        }

        var enabled = entry.Properties.FirstOrDefault(property =>
            property.IsModified &&
            string.Equals(property.Metadata.Name, "IsEnabled", StringComparison.Ordinal));
        if (enabled?.CurrentValue is bool isEnabled)
        {
            return $"{resourceType}.{(isEnabled ? "enabled" : "disabled")}";
        }

        var revoked = entry.Properties.FirstOrDefault(property =>
            property.IsModified &&
            string.Equals(property.Metadata.Name, "RevokedAtUtc", StringComparison.Ordinal));
        if (revoked?.CurrentValue is not null)
        {
            return $"{resourceType}.revoked";
        }

        return $"{resourceType}.updated";
    }

    private static AuditOutcome Outcome(EntityEntry entry)
    {
        if (entry.State != EntityState.Modified)
        {
            return AuditOutcome.Succeeded;
        }

        var status = entry.Properties.FirstOrDefault(property =>
            property.IsModified &&
            property.Metadata.Name is "Status" or "State");
        var value = status?.CurrentValue?.ToString();
        return value is "Failed" or "Conflict" or "DeadLettered"
            ? AuditOutcome.Failed
            : AuditOutcome.Succeeded;
    }

    private static string ResourceType(EntityEntry entry) => entry.Entity switch
    {
        THub.Domain.Workflows.WorkflowDefinition => "workflow",
        THub.Domain.Workflows.WorkflowVersion => "workflow-version",
        WorkflowRun => "workflow-run",
        WorkflowStepRun => "workflow-step",
        THub.Domain.Connections.DataConnection => "connection",
        THub.Domain.Alerts.EmailDeliveryProfile => "email-profile",
        THub.Domain.Alerts.WorkflowAlertRule => "email-rule",
        THub.Domain.Alerts.AlertDelivery => "email-delivery",
        THub.Domain.Publications.Publication => "publication",
        THub.Domain.Publications.PublicationVersion => "publication-version",
        THub.Domain.Publications.PublicationColumn => "publication-column",
        THub.Domain.Publications.PublicationGrant => "publication-grant",
        THub.Domain.Publications.PublicationAccessToken => "publication-token",
        THub.Domain.Publications.PublicationChangeSet => "publication-change-set",
        THub.Domain.Publications.PublicationChange => "publication-change",
        THub.Domain.Security.AccessRole => "access-role",
        THub.Domain.Security.AccessRolePermission => "access-role-permission",
        THub.Domain.Security.AccessRoleAssignment => "access-role-assignment",
        THub.Domain.Security.AccessResourceGrant => "access-resource-grant",
        THub.Domain.Actions.TrustedAction => "trusted-action",
        _ => MachineName(entry.Metadata.ClrType.Name),
    };

    private static string? ResourceIdentifier(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key?.Properties.Count != 1)
        {
            return null;
        }

        var property = entry.Property(key.Properties[0].Name);
        var value = entry.State == EntityState.Deleted
            ? property.OriginalValue
            : property.CurrentValue;
        return value is Guid id && id != Guid.Empty ? id.ToString("D") : null;
    }

    private static string? CorrelationIdentifier(EntityEntry entry)
    {
        foreach (var name in CorrelationProperties)
        {
            var property = entry.Properties.FirstOrDefault(candidate =>
                string.Equals(candidate.Metadata.Name, name, StringComparison.Ordinal));
            var value = entry.State == EntityState.Deleted
                ? property?.OriginalValue
                : property?.CurrentValue;
            if (value is Guid id && id != Guid.Empty)
            {
                return id.ToString("D");
            }
        }

        return null;
    }

    private static string NormalizeSource(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized.Length <= AuditRecord.MaximumSourceLength
            ? normalized
            : normalized[..AuditRecord.MaximumSourceLength];
    }

    private static string MachineName(string value)
    {
        var characters = value
            .Select(character => char.IsAsciiLetterOrDigit(character)
                ? char.ToLowerInvariant(character)
                : '-')
            .ToArray();
        return new string(characters).Trim('-');
    }
}
