using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using THub.Domain.Alerts;
using THub.Domain.Runs;
using THub.Domain.Workflows;

namespace THub.Infrastructure.Persistence.Configurations;

internal sealed class WorkflowDefinitionConfiguration
    : IEntityTypeConfiguration<WorkflowDefinition>
{
    public void Configure(EntityTypeBuilder<WorkflowDefinition> entity)
    {
        entity.ToTable("Workflows");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Name).HasMaxLength(WorkflowDefinition.MaximumNameLength).IsRequired();
        entity.Property(x => x.Description).HasMaxLength(WorkflowDefinition.MaximumDescriptionLength);
        entity.Property(x => x.Owner).HasMaxLength(WorkflowDefinition.MaximumOwnerLength).IsRequired();
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        entity.Property(x => x.GraphJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(x => x.CronExpression).HasMaxLength(WorkflowDefinition.MaximumCronExpressionLength);
        entity.Property(x => x.TimeZoneId).HasMaxLength(WorkflowDefinition.MaximumTimeZoneIdLength).IsRequired();
        entity.Property<byte[]>("RowVersion").IsRowVersion();
        entity.HasIndex(x => new { x.Status, x.NextRunAtUtc });
        entity.HasIndex(x => x.Name);
        entity.HasOne<WorkflowVersion>()
            .WithMany()
            .HasForeignKey(x => x.PublishedVersionId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class WorkflowVersionConfiguration : IEntityTypeConfiguration<WorkflowVersion>
{
    public void Configure(EntityTypeBuilder<WorkflowVersion> entity)
    {
        entity.ToTable("WorkflowVersions");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.GraphJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(x => x.Checksum).HasMaxLength(64).IsFixedLength().IsRequired();
        entity.Property(x => x.PublishedBy).HasMaxLength(256).IsRequired();
        entity.HasIndex(x => new { x.WorkflowId, x.Version }).IsUnique();
        entity.HasOne<WorkflowDefinition>()
            .WithMany()
            .HasForeignKey(x => x.WorkflowId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WorkflowRunConfiguration : IEntityTypeConfiguration<WorkflowRun>
{
    public void Configure(EntityTypeBuilder<WorkflowRun> entity)
    {
        entity.ToTable("WorkflowRuns");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        entity.Property(x => x.TriggeredBy).HasMaxLength(WorkflowRun.MaximumTriggeredByLength).IsRequired();
        entity.Property(x => x.LeaseOwner).HasMaxLength(WorkflowRun.MaximumLeaseOwnerLength);
        entity.Property(x => x.CancellationRequestedBy).HasMaxLength(WorkflowRun.MaximumTriggeredByLength);
        entity.Property(x => x.Error)
            .HasColumnName("ErrorJson")
            .HasColumnType("nvarchar(2048)")
            .HasConversion(new ExecutionErrorConverter());
        entity.Property(x => x.ErrorMessage).HasMaxLength(4_000);
        entity.Property<byte[]>("RowVersion").IsRowVersion();
        entity.HasIndex(x => new { x.Status, x.QueuedAtUtc });
        entity.HasIndex(x => new { x.Status, x.LeaseExpiresAtUtc });
        entity.HasIndex(x => new { x.WorkflowId, x.WorkflowVersion, x.ScheduledForUtc })
            .IsUnique()
            .HasFilter("[ScheduledForUtc] IS NOT NULL");
        entity.HasOne<WorkflowDefinition>()
            .WithMany()
            .HasForeignKey(x => x.WorkflowId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<WorkflowVersion>()
            .WithMany()
            .HasForeignKey(x => x.WorkflowVersionId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<WorkflowRun>()
            .WithMany()
            .HasForeignKey(x => x.RetryOfRunId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class WorkflowStepRunConfiguration : IEntityTypeConfiguration<WorkflowStepRun>
{
    public void Configure(EntityTypeBuilder<WorkflowStepRun> entity)
    {
        entity.ToTable("WorkflowStepRuns");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.NodeId).HasMaxLength(WorkflowStepRun.MaximumNodeIdLength).IsRequired();
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        entity.Property(x => x.Error)
            .HasColumnName("ErrorJson")
            .HasColumnType("nvarchar(2048)")
            .HasConversion(new ExecutionErrorConverter());
        entity.Property<byte[]>("RowVersion").IsRowVersion();
        entity.HasIndex(x => new { x.WorkflowRunId, x.NodeId, x.Attempt }).IsUnique();
        entity.HasIndex(x => new { x.Status, x.QueuedAtUtc });
        entity.HasOne<WorkflowRun>()
            .WithMany()
            .HasForeignKey(x => x.WorkflowRunId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class EmailDeliveryProfileConfiguration
    : IEntityTypeConfiguration<EmailDeliveryProfile>
{
    public void Configure(EntityTypeBuilder<EmailDeliveryProfile> entity)
    {
        entity.ToTable("EmailDeliveryProfiles");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
        entity.Property(x => x.SmtpHost).HasMaxLength(253).IsRequired();
        entity.Property(x => x.TransportSecurity).HasConversion<string>().HasMaxLength(32);
        entity.Property(x => x.SenderAddress).HasMaxLength(320).IsRequired();
        entity.Property(x => x.CredentialSecretReference).HasMaxLength(500);
        entity.Property(x => x.CreatedBy).HasMaxLength(256).IsRequired();

        var domains = entity.Property(x => x.AllowedRecipientDomains)
            .HasField("_allowedRecipientDomains")
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .HasColumnName("AllowedRecipientDomainsJson")
            .HasColumnType("nvarchar(max)")
            .HasConversion(new StringListConverter());
        domains.Metadata.SetValueComparer(StringListComparer.Instance);

        entity.Property(x => x.Limits)
            .HasColumnName("LimitsJson")
            .HasColumnType("nvarchar(1000)")
            .HasConversion(new EmailDeliveryLimitsConverter());
        entity.Property<byte[]>("RowVersion").IsRowVersion();
        entity.HasIndex(x => x.Name).IsUnique();
    }
}

internal sealed class WorkflowAlertRuleConfiguration : IEntityTypeConfiguration<WorkflowAlertRule>
{
    public void Configure(EntityTypeBuilder<WorkflowAlertRule> entity)
    {
        entity.ToTable("WorkflowAlertRules");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
        entity.Property(x => x.Triggers).HasConversion<int>();
        entity.Property(x => x.CreatedBy).HasMaxLength(256).IsRequired();

        var recipients = entity.Property(x => x.Recipients)
            .HasField("_recipients")
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .HasColumnName("RecipientsJson")
            .HasColumnType("nvarchar(max)")
            .HasConversion(new StringListConverter());
        recipients.Metadata.SetValueComparer(StringListComparer.Instance);

        entity.Property(x => x.Template)
            .HasColumnName("TemplateJson")
            .HasColumnType("nvarchar(max)")
            .HasConversion(new EmailTemplateConverter());
        entity.Property<byte[]>("RowVersion").IsRowVersion();
        entity.HasIndex(x => new { x.WorkflowId, x.Name }).IsUnique();
        entity.HasIndex(x => new { x.WorkflowId, x.IsEnabled });
        entity.HasOne<WorkflowDefinition>()
            .WithMany()
            .HasForeignKey(x => x.WorkflowId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<EmailDeliveryProfile>()
            .WithMany()
            .HasForeignKey(x => x.EmailDeliveryProfileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AlertDeliveryConfiguration : IEntityTypeConfiguration<AlertDelivery>
{
    public void Configure(EntityTypeBuilder<AlertDelivery> entity)
    {
        entity.ToTable("AlertDeliveries");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Source).HasConversion<string>().HasMaxLength(32);
        entity.Property(x => x.Event).HasConversion<string>().HasMaxLength(32);
        entity.Property(x => x.DeduplicationKey)
            .HasMaxLength(AlertDelivery.MaximumDeduplicationKeyLength)
            .IsRequired();
        entity.Property(x => x.StableMessageId).HasMaxLength(100).IsRequired();
        entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        entity.Property(x => x.LeaseOwner).HasMaxLength(AlertDelivery.MaximumLeaseOwnerLength);
        entity.Property(x => x.ProviderMessageId).HasMaxLength(500);
        entity.Property(x => x.WorkflowNodeId).HasMaxLength(WorkflowStepRun.MaximumNodeIdLength);
        entity.Property(x => x.Message)
            .HasColumnName("MessageJson")
            .HasColumnType("nvarchar(max)")
            .HasConversion(new EmailMessageConverter());
        entity.Property(x => x.LastError)
            .HasColumnName("LastErrorJson")
            .HasColumnType("nvarchar(2048)")
            .HasConversion(new ExecutionErrorConverter());
        entity.Property<byte[]>("RowVersion").IsRowVersion();
        entity.HasIndex(x => x.DeduplicationKey).IsUnique();
        entity.HasIndex(x => new { x.Status, x.NextAttemptAtUtc, x.LeaseExpiresAtUtc });
        entity.HasOne<WorkflowRun>()
            .WithMany()
            .HasForeignKey(x => x.WorkflowRunId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<EmailDeliveryProfile>()
            .WithMany()
            .HasForeignKey(x => x.EmailDeliveryProfileId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<WorkflowAlertRule>()
            .WithMany()
            .HasForeignKey(x => x.WorkflowAlertRuleId)
            .OnDelete(DeleteBehavior.NoAction);
        entity.HasOne<WorkflowStepRun>()
            .WithMany()
            .HasForeignKey(x => x.WorkflowStepRunId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class ExecutionErrorConverter()
    : ValueConverter<ExecutionError?, string?>(
        value => value == null ? null : Serialization.SerializeError(value),
        value => value == null ? null : Serialization.DeserializeError(value));

internal sealed class EmailDeliveryLimitsConverter()
    : ValueConverter<EmailDeliveryLimits, string>(
        value => Serialization.SerializeLimits(value),
        value => Serialization.DeserializeLimits(value));

internal sealed class EmailTemplateConverter()
    : ValueConverter<EmailTemplate, string>(
        value => Serialization.SerializeTemplate(value),
        value => Serialization.DeserializeTemplate(value));

internal sealed class EmailMessageConverter()
    : ValueConverter<EmailMessage, string>(
        value => Serialization.SerializeMessage(value),
        value => Serialization.DeserializeMessage(value));

internal sealed class StringListConverter()
    : ValueConverter<IReadOnlyList<string>, string>(
        value => Serialization.SerializeStrings(value),
        value => Serialization.DeserializeStrings(value));

internal sealed class StringListComparer()
    : ValueComparer<IReadOnlyList<string>>(
        (left, right) => ReferenceEquals(left, right)
            || left != null && right != null && left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase),
        value => value.Aggregate(0, (hash, item) => HashCode.Combine(hash, StringComparer.OrdinalIgnoreCase.GetHashCode(item))),
        value => value.ToArray())
{
    public static StringListComparer Instance { get; } = new();
}

internal static class Serialization
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string SerializeError(ExecutionError value) => JsonSerializer.Serialize(
        new ErrorContract(value.Code, value.Category, value.Summary, value.IsRetryable),
        Options);

    public static ExecutionError DeserializeError(string value)
    {
        var contract = JsonSerializer.Deserialize<ErrorContract>(value, Options)
            ?? throw new InvalidOperationException("Persisted execution error JSON is empty.");
        return new ExecutionError(contract.Code, contract.Category, contract.Summary, contract.IsRetryable);
    }

    public static string SerializeLimits(EmailDeliveryLimits value) => JsonSerializer.Serialize(
        new LimitsContract(
            value.MaximumRecipients,
            value.MaximumSubjectLength,
            value.MaximumBodyLength,
            value.MaximumConcurrentSends),
        Options);

    public static EmailDeliveryLimits DeserializeLimits(string value)
    {
        var contract = JsonSerializer.Deserialize<LimitsContract>(value, Options)
            ?? throw new InvalidOperationException("Persisted Email delivery limits JSON is empty.");
        return new EmailDeliveryLimits(
            contract.MaximumRecipients,
            contract.MaximumSubjectLength,
            contract.MaximumBodyLength,
            contract.MaximumConcurrentSends);
    }

    public static string SerializeTemplate(EmailTemplate value) => JsonSerializer.Serialize(
        new TemplateContract(value.Subject, value.Body),
        Options);

    public static EmailTemplate DeserializeTemplate(string value)
    {
        var contract = JsonSerializer.Deserialize<TemplateContract>(value, Options)
            ?? throw new InvalidOperationException("Persisted Email template JSON is empty.");
        return new EmailTemplate(contract.Subject, contract.Body);
    }

    public static string SerializeMessage(EmailMessage value) => JsonSerializer.Serialize(
        new MessageContract(value.Recipients.ToArray(), value.Subject, value.Body),
        Options);

    public static EmailMessage DeserializeMessage(string value)
    {
        var contract = JsonSerializer.Deserialize<MessageContract>(value, Options)
            ?? throw new InvalidOperationException("Persisted Email message JSON is empty.");
        return new EmailMessage(contract.Recipients, contract.Subject, contract.Body);
    }

    public static string SerializeStrings(IReadOnlyList<string> value) =>
        JsonSerializer.Serialize(value, Options);

    public static IReadOnlyList<string> DeserializeStrings(string value) =>
        Array.AsReadOnly(JsonSerializer.Deserialize<string[]>(value, Options) ?? []);

    private sealed record ErrorContract(
        string Code,
        ExecutionErrorCategory Category,
        string Summary,
        bool IsRetryable);

    private sealed record LimitsContract(
        int MaximumRecipients,
        int MaximumSubjectLength,
        int MaximumBodyLength,
        int MaximumConcurrentSends);

    private sealed record TemplateContract(string Subject, string Body);

    private sealed record MessageContract(string[] Recipients, string Subject, string Body);
}
