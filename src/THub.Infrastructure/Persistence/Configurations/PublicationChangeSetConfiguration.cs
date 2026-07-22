using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using THub.Domain.Publications;

namespace THub.Infrastructure.Persistence.Configurations;

public sealed class PublicationChangeSetConfiguration : IEntityTypeConfiguration<PublicationChangeSet>
{
    public void Configure(EntityTypeBuilder<PublicationChangeSet> builder)
    {
        builder.ToTable("PublicationChangeSets");
        builder.HasKey(changeSet => changeSet.Id);

        builder.Property(changeSet => changeSet.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(changeSet => changeSet.SubmittedBy)
            .HasMaxLength(Publication.MaximumIdentityLength)
            .IsRequired();
        builder.Property(changeSet => changeSet.ReviewedBy)
            .HasMaxLength(Publication.MaximumIdentityLength);
        builder.Property(changeSet => changeSet.ReviewComment)
            .HasMaxLength(PublicationChangeSet.MaximumCommentLength);
        builder.Property(changeSet => changeSet.ApplyStartedBy)
            .HasMaxLength(Publication.MaximumIdentityLength);
        builder.Property(changeSet => changeSet.OutcomeDetail)
            .HasMaxLength(PublicationChangeSet.MaximumCommentLength);

        builder.Property<byte[]>("RowVersion")
            .IsRowVersion();

        builder.Ignore(changeSet => changeSet.Changes);
        builder.HasMany<PublicationChange>("_changes")
            .WithOne()
            .HasForeignKey(change => change.ChangeSetId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation("_changes")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(changeSet => new { changeSet.Status, changeSet.UpdatedAtUtc });
        builder.HasIndex(changeSet => changeSet.PublicationId);
        builder.HasIndex(changeSet => changeSet.PublicationVersionId);

        builder.HasOne<Publication>()
            .WithMany()
            .HasForeignKey(changeSet => changeSet.PublicationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PublicationVersion>()
            .WithMany()
            .HasForeignKey(changeSet => changeSet.PublicationVersionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
