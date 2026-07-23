using Microsoft.EntityFrameworkCore;
using THub.Application.Security;
using THub.Application.Workflows.Management;
using THub.Domain.Security;
using THub.Domain.Workflows;
using THub.Infrastructure.Persistence;
using THub.Infrastructure.Workflows;

namespace THub.Infrastructure.Tests;

public sealed class SqlWorkflowManagementRepositoryIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task DeleteUnusedDraftAlsoRemovesItsResourceGrants()
    {
        var databaseName = $"THub_WorkflowDelete_{Guid.NewGuid():N}";
        var connectionString =
            $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Integrated Security=true;Encrypt=false";
        var options = new DbContextOptionsBuilder<THubDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        var factory = new TestDbContextFactory(options);

        try
        {
            var workflow = new WorkflowDefinition(
                "Disposable draft",
                "CONTOSO\\owner",
                """{"schemaVersion":2,"variables":[],"functions":[],"nodes":[],"edges":[]}""",
                DateTimeOffset.UtcNow);
            var grant = new AccessResourceGrant(
                Guid.NewGuid(),
                SystemRoleIds.Developer,
                AccessResourceKind.Workflow,
                workflow.Id,
                SecurityPermissions.WorkflowDelete);
            await using (var setup = factory.CreateDbContext())
            {
                await setup.Database.MigrateAsync();
                setup.Workflows.Add(workflow);
                setup.AccessResourceGrants.Add(grant);
                await setup.SaveChangesAsync();
            }

            var result = await new SqlWorkflowManagementRepository(factory).DeleteWorkflowAsync(
                workflow.Id,
                workflow.DraftRevision,
                CancellationToken.None);

            Assert.Equal(WorkflowStoreWriteStatus.Succeeded, result.Status);
            await using var verification = factory.CreateDbContext();
            Assert.False(await verification.Workflows.AnyAsync(
                candidate => candidate.Id == workflow.Id));
            Assert.False(await verification.AccessResourceGrants.AnyAsync(
                candidate => candidate.ResourceId == workflow.Id));
        }
        finally
        {
            await using var cleanup = factory.CreateDbContext();
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<THubDbContext> options)
        : IDbContextFactory<THubDbContext>
    {
        public THubDbContext CreateDbContext() => new(options);
    }
}
