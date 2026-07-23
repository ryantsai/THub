using Microsoft.Extensions.Configuration;
using THub.Infrastructure.Connections;

namespace THub.Infrastructure.Tests;

public sealed class ConfigurationDatabaseCredentialResolverTests
{
    [Fact]
    public async Task ResolveAsync_ReadsReferencedCredentialWithoutPersistingIt()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionCredentials:warehouse_reader:Username"] = "db_reader",
                ["ConnectionCredentials:warehouse_reader:Password"] = "secret-value"
            })
            .Build();
        var resolver = new ConfigurationDatabaseCredentialResolver(configuration);

        var credential = await resolver.ResolveAsync(
            "warehouse_reader",
            CancellationToken.None);

        Assert.NotNull(credential);
        Assert.Equal("db_reader", credential.UserName);
        Assert.Equal("secret-value", credential.Password);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNullWhenReferenceIsNotProvisioned()
    {
        var resolver = new ConfigurationDatabaseCredentialResolver(
            new ConfigurationBuilder().Build());

        var credential = await resolver.ResolveAsync(
            "missing",
            CancellationToken.None);

        Assert.Null(credential);
    }
}
