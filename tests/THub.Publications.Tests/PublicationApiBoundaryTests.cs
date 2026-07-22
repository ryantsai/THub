using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;

namespace THub.Publications.Tests;

[Collection(PublicationHostCollection.Name)]
public sealed class PublicationApiBoundaryTests
{
    private readonly HttpClient _client;

    public PublicationApiBoundaryTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
    }

    [Fact]
    public async Task MissingBearerCredentialReturnsGenericChallenge()
    {
        using var response = await _client.GetAsync(
            "/api/v1/publications/customer-lookup/rows",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.Contains("invalid or unavailable", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("customer-lookup", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TokenInQueryStringIsRejectedBeforeCredentialLookup()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/publications/customer-lookup/rows?token=secret");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "invalid");

        using var response = await _client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.Contains("publication.query_invalid", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultipleAuthorizationValuesDoNotAuthenticate()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/publications/customer-lookup/schema");
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            ["Bearer thub_one.two", "Bearer thub_three.four"]);

        using var response = await _client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DuplicateSortAliasesAreRejectedIgnoringCaseAndDirection()
    {
        using var response = await _client.GetAsync(
            "/api/v1/publications/customer-lookup/rows?sort=customerId&sort=-CUSTOMERID",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.Contains("publication.query_invalid", body, StringComparison.Ordinal);
        Assert.Contains("at most once", body, StringComparison.OrdinalIgnoreCase);
    }
}
