using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using THub.Domain.Publications;

namespace THub.Infrastructure.Persistence.Configurations;

public sealed class PublicationConfiguration : IEntityTypeConfiguration<Publication>
{
    public void Configure(EntityTypeBuilder<Publication> builder)
    {
        builder.ToTable("Publications");
        builder.HasKey(publication => publication.Id);

        builder.Property(publication => publication.Slug)
            .HasMaxLength(Publication.MaximumSlugLength)
            .IsRequired();
        builder.Property(publication => publication.Name)
            .HasMaxLength(Publication.MaximumNameLength)
            .IsRequired();
        builder.Property(publication => publication.Kind)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(publication => publication.State)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(publication => publication.CreatedBy)
            .HasMaxLength(Publication.MaximumIdentityLength)
            .IsRequired();
        builder.Property(publication => publication.UpdatedBy)
            .HasMaxLength(Publication.MaximumIdentityLength)
            .IsRequired();

        builder.Property<byte[]>("RowVersion")
            .IsRowVersion();

        builder.HasIndex(publication => publication.Slug)
            .IsUnique();
        builder.HasIndex(publication => new { publication.Kind, publication.State });
        builder.HasIndex(publication => publication.ActiveVersionId);

        builder.HasOne<PublicationVersion>()
            .WithMany()
            .HasForeignKey(publication => publication.ActiveVersionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
