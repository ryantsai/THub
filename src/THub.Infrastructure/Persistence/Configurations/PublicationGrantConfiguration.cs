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

        builder.HasIndex(grant => new { grant.PublicationId, grant.RoleId })
            .IsUnique();

        builder.HasOne<Publication>()
            .WithMany()
            .HasForeignKey(grant => grant.PublicationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<THub.Domain.Security.AccessRole>()
            .WithMany()
            .HasForeignKey(grant => grant.RoleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
