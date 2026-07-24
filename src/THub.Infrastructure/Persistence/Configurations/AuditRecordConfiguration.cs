using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using THub.Domain.Auditing;

namespace THub.Infrastructure.Persistence.Configurations;

public sealed class AuditRecordConfiguration : IEntityTypeConfiguration<AuditRecord>
{
    public void Configure(EntityTypeBuilder<AuditRecord> builder)
    {
        builder.ToTable("AuditRecords", table =>
            table.HasTrigger("TR_AuditRecords_AppendOnly"));
        builder.HasKey(record => record.Id);
        builder.Property(record => record.ActorKind)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(record => record.ActorIdentifier)
            .HasMaxLength(AuditRecord.MaximumActorIdentifierLength)
            .IsRequired();
        builder.Property(record => record.Source)
            .HasMaxLength(AuditRecord.MaximumSourceLength)
            .IsRequired();
        builder.Property(record => record.Action)
            .HasMaxLength(AuditRecord.MaximumActionLength)
            .IsRequired();
        builder.Property(record => record.Outcome)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(record => record.ResourceType)
            .HasMaxLength(AuditRecord.MaximumResourceTypeLength)
            .IsRequired();
        builder.Property(record => record.ResourceIdentifier)
            .HasMaxLength(AuditRecord.MaximumResourceIdentifierLength);
        builder.Property(record => record.CorrelationIdentifier)
            .HasMaxLength(AuditRecord.MaximumCorrelationIdentifierLength);
        builder.HasIndex(record => record.OccurredAtUtc);
        builder.HasIndex(record => new { record.Action, record.OccurredAtUtc });
        builder.HasIndex(record => new { record.ActorIdentifier, record.OccurredAtUtc });
        builder.HasIndex(record => new
        {
            record.ResourceType,
            record.ResourceIdentifier,
            record.OccurredAtUtc,
        });
    }
}
