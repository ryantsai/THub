using Microsoft.EntityFrameworkCore;
using THub.Application.Security;
using THub.Domain.Security;
using THub.Infrastructure.Persistence;
using THub.Infrastructure.Security;

namespace THub.Infrastructure.Tests;

public sealed class SqlAccessControlStoreIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task SaveCustomRoleRejectsGrantForMissingResource()
    {
        var databaseName = $"THub_AccessControl_{Guid.NewGuid():N}";
        var connectionString =
            $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Integrated Security=true;Encrypt=false";
        var options = new DbContextOptionsBuilder<THubDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        var factory = new TestDbContextFactory(options);

        try
        {
            await using (var setup = factory.CreateDbContext())
            {
                await setup.Database.MigrateAsync();
            }

            var roleId = Guid.NewGuid();
            var role = new AccessRole(
                roleId,
                "Scoped workflow reader",
                "Can view one workflow.",
                null,
                DateTimeOffset.UtcNow,
                "integration-test");
            var grant = new AccessResourceGrant(
                Guid.NewGuid(),
                roleId,
                AccessResourceKind.Workflow,
                Guid.NewGuid(),
                SecurityPermissions.WorkflowView);

            var status = await new SqlAccessControlStore(factory).SaveCustomRoleAsync(
                role,
                [],
                [],
                [grant],
                CancellationToken.None);

            Assert.Equal(AccessRoleWriteStatus.NotFound, status);
            await using var verification = factory.CreateDbContext();
            Assert.False(await verification.AccessRoles.AnyAsync(candidate => candidate.Id == roleId));
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
