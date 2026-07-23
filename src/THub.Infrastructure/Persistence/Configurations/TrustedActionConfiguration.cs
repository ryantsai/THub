using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using THub.Domain.Actions;

namespace THub.Infrastructure.Persistence.Configurations;

public sealed class TrustedActionConfiguration : IEntityTypeConfiguration<TrustedAction>
{
    public void Configure(EntityTypeBuilder<TrustedAction> builder)
    {
        builder.ToTable("TrustedActions");
        builder.HasKey(action => action.Id);
        builder.Property(action => action.Name)
            .HasMaxLength(TrustedAction.MaximumNameLength)
            .IsRequired();
        builder.Property(action => action.Kind)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(action => action.DefinitionJson)
            .HasColumnType("nvarchar(max)")
            .IsRequired();
        builder.Property(action => action.CredentialReference)
            .HasMaxLength(TrustedAction.MaximumCredentialReferenceLength);
        builder.Property(action => action.CreatedBy)
            .HasMaxLength(TrustedAction.MaximumIdentityLength)
            .IsRequired();
        builder.Property(action => action.UpdatedBy)
            .HasMaxLength(TrustedAction.MaximumIdentityLength)
            .IsRequired();
        builder.Property<byte[]>("RowVersion").IsRowVersion();
        builder.HasIndex(action => action.Name).IsUnique();
        builder.HasIndex(action => new { action.Kind, action.IsEnabled });
    }
}
