using Microsoft.EntityFrameworkCore;
using THub.Domain.Alerts;
using THub.Domain.Connections;
using THub.Domain.Publications;
using THub.Domain.Runs;
using THub.Domain.Security;
using THub.Domain.Workflows;
using THub.Infrastructure.Connections;

namespace THub.Infrastructure.Persistence;

public sealed class THubDbContext(DbContextOptions<THubDbContext> options) : DbContext(options)
{
    public DbSet<WorkflowDefinition> Workflows => Set<WorkflowDefinition>();
    public DbSet<WorkflowVersion> WorkflowVersions => Set<WorkflowVersion>();
    public DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();
    public DbSet<WorkflowStepRun> WorkflowStepRuns => Set<WorkflowStepRun>();
    public DbSet<DataConnection> Connections => Set<DataConnection>();
    internal DbSet<EncryptedConnectionCredential> EncryptedConnectionCredentials =>
        Set<EncryptedConnectionCredential>();
    public DbSet<EmailDeliveryProfile> EmailDeliveryProfiles => Set<EmailDeliveryProfile>();
    public DbSet<WorkflowAlertRule> WorkflowAlertRules => Set<WorkflowAlertRule>();
    public DbSet<AlertDelivery> AlertDeliveries => Set<AlertDelivery>();
    public DbSet<Publication> Publications => Set<Publication>();
    public DbSet<PublicationVersion> PublicationVersions => Set<PublicationVersion>();
    public DbSet<PublicationColumn> PublicationColumns => Set<PublicationColumn>();
    public DbSet<PublicationGrant> PublicationGrants => Set<PublicationGrant>();
    public DbSet<PublicationAccessToken> PublicationAccessTokens => Set<PublicationAccessToken>();
    public DbSet<PublicationChangeSet> PublicationChangeSets => Set<PublicationChangeSet>();
    public DbSet<PublicationChange> PublicationChanges => Set<PublicationChange>();
    public DbSet<AccessRole> AccessRoles => Set<AccessRole>();
    public DbSet<AccessRolePermission> AccessRolePermissions => Set<AccessRolePermission>();
    public DbSet<AccessRoleAssignment> AccessRoleAssignments => Set<AccessRoleAssignment>();
    public DbSet<AccessResourceGrant> AccessResourceGrants => Set<AccessResourceGrant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("thub");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(THubDbContext).Assembly);

        modelBuilder.Entity<DataConnection>(entity =>
        {
            entity.ToTable("Connections");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(DataConnection.MaximumNameLength).IsRequired();
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.ConfigurationJson).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(DataConnection.MaximumIdentityLength).IsRequired();
            entity.Property<byte[]>("RowVersion").IsRowVersion();
            entity.HasIndex(x => x.Name).IsUnique();
            entity.HasIndex(x => new { x.Kind, x.IsEnabled });
        });
    }
}
