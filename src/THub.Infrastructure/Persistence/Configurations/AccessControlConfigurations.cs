using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using THub.Domain.Security;

namespace THub.Infrastructure.Persistence.Configurations;

public sealed class AccessRoleConfiguration : IEntityTypeConfiguration<AccessRole>
{
    public void Configure(EntityTypeBuilder<AccessRole> builder)
    {
        builder.ToTable("AccessRoles");
        builder.HasKey(role => role.Id);
        builder.Property(role => role.Name).HasMaxLength(AccessRole.MaximumNameLength).IsRequired();
        builder.Property(role => role.Description).HasMaxLength(AccessRole.MaximumDescriptionLength).IsRequired();
        builder.Property(role => role.SystemRole).HasConversion<string>().HasMaxLength(32);
        builder.Property(role => role.CreatedBy).HasMaxLength(256).IsRequired();
        builder.HasIndex(role => role.Name).IsUnique();
        builder.HasIndex(role => role.SystemRole).IsUnique().HasFilter("[SystemRole] IS NOT NULL");
    }
}

public sealed class AccessRolePermissionConfiguration : IEntityTypeConfiguration<AccessRolePermission>
{
    public void Configure(EntityTypeBuilder<AccessRolePermission> builder)
    {
        builder.ToTable("AccessRolePermissions");
        builder.HasKey(permission => permission.Id);
        builder.Property(permission => permission.Permission)
            .HasMaxLength(AccessRolePermission.MaximumPermissionLength)
            .IsRequired();
        builder.HasIndex(permission => new { permission.RoleId, permission.Permission }).IsUnique();
        builder.HasOne<AccessRole>()
            .WithMany()
            .HasForeignKey(permission => permission.RoleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class AccessRoleAssignmentConfiguration : IEntityTypeConfiguration<AccessRoleAssignment>
{
    public void Configure(EntityTypeBuilder<AccessRoleAssignment> builder)
    {
        builder.ToTable("AccessRoleAssignments");
        builder.HasKey(assignment => assignment.Id);
        builder.Property(assignment => assignment.PrincipalKind)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(assignment => assignment.PrincipalName)
            .HasMaxLength(AccessRoleAssignment.MaximumPrincipalNameLength)
            .IsRequired();
        builder.Property(assignment => assignment.NormalizedPrincipalName)
            .HasMaxLength(AccessRoleAssignment.MaximumPrincipalNameLength)
            .IsRequired();
        builder.HasIndex(assignment => new
        {
            assignment.RoleId,
            assignment.PrincipalKind,
            assignment.NormalizedPrincipalName,
        }).IsUnique();
        builder.HasOne<AccessRole>()
            .WithMany()
            .HasForeignKey(assignment => assignment.RoleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class AccessResourceGrantConfiguration : IEntityTypeConfiguration<AccessResourceGrant>
{
    public void Configure(EntityTypeBuilder<AccessResourceGrant> builder)
    {
        builder.ToTable("AccessResourceGrants");
        builder.HasKey(grant => grant.Id);
        builder.Property(grant => grant.ResourceKind)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(grant => grant.Permission)
            .HasMaxLength(AccessRolePermission.MaximumPermissionLength)
            .IsRequired();
        builder.HasIndex(grant => new
        {
            grant.RoleId,
            grant.ResourceKind,
            grant.ResourceId,
            grant.Permission,
        }).IsUnique();
        builder.HasIndex(grant => new { grant.ResourceKind, grant.ResourceId, grant.Permission });
        builder.HasOne<AccessRole>()
            .WithMany()
            .HasForeignKey(grant => grant.RoleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
