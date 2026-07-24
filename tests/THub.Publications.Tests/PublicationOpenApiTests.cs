using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace THub.Publications.Tests;

[Collection(PublicationHostCollection.Name)]
public sealed class PublicationOpenApiTests
{
    private readonly HttpClient _client;

    public PublicationOpenApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
    }

    [Fact]
    public async Task OpenApiDocumentDescribesBearerRowsSchemaAndQueryContract()
    {
        using var response = await _client.GetAsync("/openapi/v1.json", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.Contains("publicationBearer", body, StringComparison.Ordinal);
        Assert.Contains("/api/v1/publications/{slug}/rows", body, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\"", body, StringComparison.Ordinal);
        Assert.Contains("\"filter\"", body, StringComparison.Ordinal);
        Assert.Contains("\"sort\"", body, StringComparison.Ordinal);
        Assert.Contains("PublicationRowsResponse", body, StringComparison.Ordinal);
        Assert.DoesNotContain("thub_", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwaggerUiIsAvailableWithoutPersistingAuthorization()
    {
        using var response = await _client.GetAsync(
            "/swagger/index.html",
            CancellationToken.None);
        using var configurationResponse = await _client.GetAsync(
            "/swagger/index.js",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, configurationResponse.StatusCode);
        var configuration = await configurationResponse.Content
            .ReadAsStringAsync(CancellationToken.None);
        Assert.Contains("openapi/v1.json", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "persistAuthorization: true",
            configuration,
            StringComparison.OrdinalIgnoreCase);
    }
}
