using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace THub.Publications.Tests;

[Collection(PublicationApiSqlCollection.Name)]
public sealed class PublicationApiSqlIntegrationTests(PublicationApiSqlFixture fixture)
{
    [Fact]
    public async Task ManagedBearerPipelineMetersIndependentlyAndServesBoundedSqlData()
    {
        await AssertGenericUnauthorizedAsync(
            fixture.PrimarySlug,
            fixture.RevokedToken.PlaintextToken);
        await AssertGenericUnauthorizedAsync(
            fixture.PrimarySlug,
            fixture.ExpiredToken.PlaintextToken);
        await AssertGenericUnauthorizedAsync(
            fixture.PrimarySlug,
            fixture.BoundedPublicationToken.PlaintextToken);

        var rejectedUsage = await fixture.ReadTokenUsageAsync();
        Assert.Equal(0, rejectedUsage[fixture.RevokedToken.Entity.Id].AcceptedRequestCount);
        Assert.Equal(0, rejectedUsage[fixture.ExpiredToken.Entity.Id].AcceptedRequestCount);
        Assert.Equal(0, rejectedUsage[fixture.BoundedPublicationToken.Entity.Id].AcceptedRequestCount);

        using (var schemaResponse = await SendAsync(
                   fixture.PrimarySlug,
                   "schema",
                   fixture.PrimaryTokenA.PlaintextToken))
        {
            Assert.Equal(HttpStatusCode.OK, schemaResponse.StatusCode);
            Assert.Equal("no-store", schemaResponse.Headers.CacheControl?.ToString());
            Assert.Equal("nosniff", GetSingleHeader(schemaResponse, "X-Content-Type-Options"));
            Assert.InRange(
                schemaResponse.Content.Headers.ContentLength ?? 0,
                1,
                1024 * 1024);

            using var schema = JsonDocument.Parse(await schemaResponse.Content.ReadAsStreamAsync());
            var root = schema.RootElement;
            Assert.NotEqual(Guid.Empty, root.GetProperty("version").GetGuid());
            Assert.Equal(
                ["id", "name", "isActive", "amount", "createdAt"],
                root.GetProperty("columns")
                    .EnumerateArray()
                    .Select(column => column.GetProperty("name").GetString() ?? string.Empty)
                    .ToArray());
            Assert.Equal("keyset", root.GetProperty("paging").GetProperty("mode").GetString());
            Assert.Equal(
                ["eq", "ne", "gt", "ge", "lt", "le", "isnull", "isnotnull"],
                root.GetProperty("filters")
                    .GetProperty("operators")
                    .GetProperty("allTypes")
                    .EnumerateArray()
                    .Select(value => value.GetString() ?? string.Empty)
                    .ToArray());
            Assert.Equal(
                ["startswith", "contains"],
                root.GetProperty("filters")
                    .GetProperty("operators")
                    .GetProperty("stringOnly")
                    .EnumerateArray()
                    .Select(value => value.GetString() ?? string.Empty)
                    .ToArray());
        }

        string cursor;
        using (var firstPageResponse = await SendAsync(
                   fixture.PrimarySlug,
                   "rows?pageSize=2&filter=isActive:eq:true&sort=-id",
                   fixture.PrimaryTokenA.PlaintextToken))
        {
            Assert.Equal(HttpStatusCode.OK, firstPageResponse.StatusCode);
            using var page = JsonDocument.Parse(await firstPageResponse.Content.ReadAsStreamAsync());
            var rows = page.RootElement.GetProperty("data").EnumerateArray().ToArray();
            Assert.Equal(2, rows.Length);
            Assert.Equal(4, rows[0].GetProperty("id").GetInt32());
            Assert.Equal("Delta", rows[0].GetProperty("name").GetString());
            Assert.True(rows[0].GetProperty("isActive").GetBoolean());
            Assert.Equal(48.00m, rows[0].GetProperty("amount").GetDecimal());
            Assert.Equal(3, rows[1].GetProperty("id").GetInt32());
            cursor = Assert.IsType<string>(page.RootElement.GetProperty("nextCursor").GetString());
            Assert.NotEmpty(cursor);
        }

        using (var secondPageResponse = await SendAsync(
                   fixture.PrimarySlug,
                   $"rows?pageSize=2&filter=isActive:eq:true&sort=-id&cursor={Uri.EscapeDataString(cursor)}",
                   fixture.PrimaryTokenA.PlaintextToken))
        {
            Assert.Equal(HttpStatusCode.OK, secondPageResponse.StatusCode);
            using var page = JsonDocument.Parse(await secondPageResponse.Content.ReadAsStreamAsync());
            var rows = page.RootElement.GetProperty("data").EnumerateArray().ToArray();
            Assert.Single(rows);
            Assert.Equal(1, rows[0].GetProperty("id").GetInt32());
            Assert.Equal(JsonValueKind.Null, page.RootElement.GetProperty("nextCursor").ValueKind);
        }

        const int tokenAConcurrentUses = 8;
        const int tokenBConcurrentUses = 5;
        var concurrentRequests = Enumerable.Range(0, tokenAConcurrentUses)
            .Select(_ => GetStatusAsync(
                fixture.PrimarySlug,
                "schema",
                fixture.PrimaryTokenA.PlaintextToken))
            .Concat(Enumerable.Range(0, tokenBConcurrentUses).Select(_ => GetStatusAsync(
                fixture.PrimarySlug,
                "schema",
                fixture.PrimaryTokenB.PlaintextToken)));
        var concurrentStatuses = await Task.WhenAll(concurrentRequests);
        Assert.All(concurrentStatuses, status => Assert.Equal(HttpStatusCode.OK, status));

        fixture.DelayNextSchemaCatalogReadBeyondRequestTimeout();
        using (var timeoutResponse = await SendAsync(
                   fixture.PrimarySlug,
                   "schema",
                   fixture.PrimaryTokenB.PlaintextToken))
        {
            Assert.Equal(HttpStatusCode.GatewayTimeout, timeoutResponse.StatusCode);
            Assert.Equal(
                "publication.request_timeout",
                await ReadProblemCodeAsync(timeoutResponse));
        }

        using (var boundedResponse = await SendAsync(
                   fixture.BoundedSlug,
                   "schema",
                   fixture.BoundedPublicationToken.PlaintextToken))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, boundedResponse.StatusCode);
            Assert.Equal(
                "publication.response_limit",
                await ReadProblemCodeAsync(boundedResponse));
        }

        var usage = await fixture.ReadTokenUsageAsync();
        Assert.Equal(11, usage[fixture.PrimaryTokenA.Entity.Id].AcceptedRequestCount);
        Assert.Equal(6, usage[fixture.PrimaryTokenB.Entity.Id].AcceptedRequestCount);
        Assert.Equal(0, usage[fixture.RevokedToken.Entity.Id].AcceptedRequestCount);
        Assert.Equal(0, usage[fixture.ExpiredToken.Entity.Id].AcceptedRequestCount);
        Assert.Equal(1, usage[fixture.BoundedPublicationToken.Entity.Id].AcceptedRequestCount);
        Assert.NotNull(usage[fixture.PrimaryTokenA.Entity.Id].LastUsedAtUtc);
        Assert.NotNull(usage[fixture.PrimaryTokenB.Entity.Id].LastUsedAtUtc);
        Assert.Null(usage[fixture.RevokedToken.Entity.Id].LastUsedAtUtc);
        Assert.Null(usage[fixture.ExpiredToken.Entity.Id].LastUsedAtUtc);
    }

    private async Task AssertGenericUnauthorizedAsync(string slug, string token)
    {
        using var response = await SendAsync(slug, "schema", token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.Equal(
            "publication.token_invalid",
            problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "The bearer token is invalid or unavailable.",
            problem.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain(slug, problem.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HttpResponseMessage> SendAsync(
        string slug,
        string relativeRoute,
        string token)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/publications/{slug}/{relativeRoute}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await fixture.Client.SendAsync(request, CancellationToken.None);
    }

    private async Task<HttpStatusCode> GetStatusAsync(
        string slug,
        string relativeRoute,
        string token)
    {
        using var response = await SendAsync(slug, relativeRoute, token);
        return response.StatusCode;
    }

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    private static string GetSingleHeader(HttpResponseMessage response, string name) =>
        Assert.Single(response.Headers.GetValues(name));
}
