using Microsoft.AspNetCore.Mvc.Testing;

namespace THub.Publications.Tests;

[Collection(PublicationHostCollection.Name)]
public sealed class HealthEndpointTests
{
    private readonly HttpClient client;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
    }

    [Fact]
    public async Task HealthEndpointIsAnonymousAndSuccessful()
    {
        using var response = await client.GetAsync("/healthz", CancellationToken.None);

        Assert.True(response.IsSuccessStatusCode);
    }
}
