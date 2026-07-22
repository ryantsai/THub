using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using THub.Domain.Publications;

namespace THub.Infrastructure.Persistence.Configurations;

public sealed class PublicationChangeConfiguration : IEntityTypeConfiguration<PublicationChange>
{
    public void Configure(EntityTypeBuilder<PublicationChange> builder)
    {
        builder.ToTable("PublicationChanges");
        builder.HasKey(change => change.Id);

        builder.Property(change => change.Operation)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(change => change.KeyJson)
            .HasColumnType("nvarchar(max)");
        builder.Property(change => change.BeforeJson)
            .HasColumnType("nvarchar(max)");
        builder.Property(change => change.AfterJson)
            .HasColumnType("nvarchar(max)");

        builder.HasIndex(change => new { change.ChangeSetId, change.Operation });
    }
}
