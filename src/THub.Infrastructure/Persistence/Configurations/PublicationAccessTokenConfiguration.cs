using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using THub.Domain.Publications;

namespace THub.Infrastructure.Persistence.Configurations;

public sealed class PublicationAccessTokenConfiguration : IEntityTypeConfiguration<PublicationAccessToken>
{
    public void Configure(EntityTypeBuilder<PublicationAccessToken> builder)
    {
        builder.ToTable("PublicationAccessTokens");
        builder.HasKey(token => token.Id);

        builder.Property(token => token.Name)
            .HasMaxLength(PublicationAccessToken.MaximumNameLength)
            .IsRequired();
        builder.Property(token => token.Selector)
            .HasMaxLength(PublicationAccessToken.MaximumSelectorLength)
            .UseCollation("Latin1_General_100_BIN2")
            .IsRequired();
        builder.Property(token => token.Verifier)
            .HasMaxLength(PublicationAccessToken.MaximumVerifierLength)
            .IsRequired();
        builder.Property(token => token.DisplayPrefix)
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(token => token.CreatedBy)
            .HasMaxLength(Publication.MaximumIdentityLength)
            .IsRequired();
        builder.Property(token => token.RevokedBy)
            .HasMaxLength(Publication.MaximumIdentityLength);
        builder.Property(token => token.AcceptedRequestCount)
            .HasDefaultValue(0L);

        builder.Property<byte[]>("RowVersion")
            .IsRowVersion();

        builder.HasIndex(token => token.Selector)
            .IsUnique();
        builder.HasIndex(token => new
        {
            token.PublicationId,
            token.RevokedAtUtc,
            token.ExpiresAtUtc,
        });
        builder.HasIndex(token => new { token.PublicationId, token.Name });

        builder.HasOne<Publication>()
            .WithMany()
            .HasForeignKey(token => token.PublicationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
