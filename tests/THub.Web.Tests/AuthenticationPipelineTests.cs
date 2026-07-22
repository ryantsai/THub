using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace THub.Web.Tests;

public sealed class AuthenticationPipelineTests
{
    [Fact]
    public async Task DevelopmentBypassRendersTheBlazorHost()
    {
        await using var factory = CreateFactory(useDevelopmentIdentity: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using var response = await client.GetAsync("/");
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("_framework/blazor.web.js", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnauthorizedResponseIsNotRewrittenAsAnErrorPage()
    {
        var context = new DefaultHttpContext();
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;

        await StatusCodePageHandler.HandleAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task NotFoundResponseRedirectsToTheNotFoundPage()
    {
        var context = new DefaultHttpContext();
        context.Response.StatusCode = StatusCodes.Status404NotFound;

        await StatusCodePageHandler.HandleAsync(context);

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal("/not-found", context.Response.Headers.Location);
    }

    private static WebApplicationFactory<Program> CreateFactory(bool useDevelopmentIdentity) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting(
                "Authentication:DevelopmentBypass",
                useDevelopmentIdentity.ToString());
        });
}
