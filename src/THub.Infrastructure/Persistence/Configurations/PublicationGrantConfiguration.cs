using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using THub.Domain.Publications;

namespace THub.Infrastructure.Persistence.Configurations;

public sealed class PublicationGrantConfiguration : IEntityTypeConfiguration<PublicationGrant>
{
    public void Configure(EntityTypeBuilder<PublicationGrant> builder)
    {
        builder.ToTable("PublicationGrants");
        builder.HasKey(grant => grant.Id);

        builder.Property(grant => grant.Role)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.HasIndex(grant => new { grant.PublicationId, grant.Role })
            .IsUnique();

        builder.HasOne<Publication>()
            .WithMany()
            .HasForeignKey(grant => grant.PublicationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
