using Microsoft.AspNetCore.Authentication.Negotiate;
using Radzen;
using Serilog;
using THub.Application;
using THub.Infrastructure;
using THub.Web;
using THub.Web.Components;
using THub.Web.Security;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    SerilogConfiguration.Configure(builder);

    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();
    builder.Services.AddCascadingAuthenticationState();
    var useDevelopmentIdentity = builder.Environment.IsDevelopment()
        && builder.Configuration.GetValue<bool>("Authentication:DevelopmentBypass");
    if (useDevelopmentIdentity)
    {
        builder.Services.AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(
                DevelopmentAuthenticationHandler.SchemeName,
                _ => { });
    }
    else
    {
        builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
            .AddNegotiate();
    }
    builder.Services.AddTHubAuthorization(builder.Configuration);
    builder.Services.AddRadzenComponents();
    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddHealthChecks();

    var app = builder.Build();

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        app.UseHsts();
    }

    app.UseStatusCodePages(statusCodeContext =>
        StatusCodePageHandler.HandleAsync(statusCodeContext.HttpContext));
    app.UseHttpsRedirection();
    app.UseSerilogRequestLogging();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseAntiforgery();

    app.MapStaticAssets();
    app.MapHealthChecks("/healthz").AllowAnonymous();
    app.MapGet("/api/v1/runtime/status", () => Results.Ok(new
    {
        service = "THub.Web",
        status = "ready",
        timestampUtc = DateTimeOffset.UtcNow
    })).RequireAuthorization(Permissions.WorkflowView);
    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();

    app.Run();
}
catch (HostAbortedException)
{
    throw;
}
catch (Exception exception)
{
    Log.Fatal(exception, "THub.Web terminated unexpectedly.");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program;
