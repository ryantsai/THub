using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using THub.Domain.Connections;
using THub.Domain.Publications;

namespace THub.Infrastructure.Persistence.Configurations;

public sealed class PublicationVersionConfiguration : IEntityTypeConfiguration<PublicationVersion>
{
    public void Configure(EntityTypeBuilder<PublicationVersion> builder)
    {
        builder.ToTable("PublicationVersions");
        builder.HasKey(version => version.Id);

        builder.Property(version => version.SourceSchema)
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(version => version.SourceObject)
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(version => version.SourceObjectKind)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(version => version.SchemaFingerprint)
            .HasMaxLength(256)
            .IsRequired();
        builder.Property(version => version.ConcurrencyMode)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(version => version.CreatedBy)
            .HasMaxLength(Publication.MaximumIdentityLength)
            .IsRequired();

        builder.Ignore(version => version.Columns);
        builder.HasMany<PublicationColumn>("_columns")
            .WithOne()
            .HasForeignKey(column => column.PublicationVersionId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation("_columns")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.OwnsOne(version => version.Settings, settings =>
        {
            settings.Property(value => value.DefaultPageSize)
                .HasColumnName("DefaultPageSize")
                .IsRequired();
            settings.Property(value => value.MaximumPageSize)
                .HasColumnName("MaximumPageSize")
                .IsRequired();
            settings.Property(value => value.RequestsPerWindow)
                .HasColumnName("RequestsPerWindow")
                .IsRequired();
            settings.Property(value => value.RateLimitWindowSeconds)
                .HasColumnName("RateLimitWindowSeconds")
                .IsRequired();
            settings.Property(value => value.MaximumConcurrentRequests)
                .HasColumnName("MaximumConcurrentRequests")
                .IsRequired();
            settings.Property(value => value.EditorWindowSize)
                .HasColumnName("EditorWindowSize")
                .IsRequired();
            settings.Property(value => value.RequestTimeoutSeconds)
                .HasColumnName("RequestTimeoutSeconds")
                .IsRequired();
            settings.Property(value => value.CommandTimeoutSeconds)
                .HasColumnName("CommandTimeoutSeconds")
                .IsRequired();
            settings.Property(value => value.MaximumResponseBytes)
                .HasColumnName("MaximumResponseBytes")
                .IsRequired();
        });
        builder.Navigation(version => version.Settings)
            .IsRequired();

        builder.HasIndex(version => new { version.PublicationId, version.VersionNumber })
            .IsUnique();
        builder.HasIndex(version => version.ConnectionId);
        builder.HasIndex(version => version.ApplyConnectionId);
        builder.HasIndex(version => new
        {
            version.ConnectionId,
            version.SourceSchema,
            version.SourceObject,
        });

        builder.HasOne<Publication>()
            .WithMany()
            .HasForeignKey(version => version.PublicationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DataConnection>()
            .WithMany()
            .HasForeignKey(version => version.ConnectionId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DataConnection>()
            .WithMany()
            .HasForeignKey(version => version.ApplyConnectionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
