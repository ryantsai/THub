using Microsoft.EntityFrameworkCore;
using THub.Domain.Connections;
using THub.Domain.Runs;
using THub.Domain.Workflows;

namespace THub.Infrastructure.Persistence;

public sealed class THubDbContext(DbContextOptions<THubDbContext> options) : DbContext(options)
{
    public DbSet<WorkflowDefinition> Workflows => Set<WorkflowDefinition>();
    public DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();
    public DbSet<DataConnection> Connections => Set<DataConnection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("thub");

        modelBuilder.Entity<WorkflowDefinition>(entity =>
        {
            entity.ToTable("Workflows");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(2_000);
            entity.Property(x => x.Owner).HasMaxLength(256).IsRequired();
            entity.Property(x => x.GraphJson).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(x => x.CronExpression).HasMaxLength(100);
            entity.Property(x => x.TimeZoneId).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            entity.HasIndex(x => new { x.Status, x.NextRunAtUtc });
            entity.HasIndex(x => x.Name);
        });

        modelBuilder.Entity<WorkflowRun>(entity =>
        {
            entity.ToTable("WorkflowRuns");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.TriggeredBy).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ErrorMessage).HasMaxLength(4_000);
            entity.HasIndex(x => new { x.Status, x.QueuedAtUtc });
            entity.HasOne<WorkflowDefinition>()
                .WithMany()
                .HasForeignKey(x => x.WorkflowId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DataConnection>(entity =>
        {
            entity.ToTable("Connections");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.ConfigurationJson).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(256).IsRequired();
            entity.HasIndex(x => x.Name).IsUnique();
        });
    }
}

