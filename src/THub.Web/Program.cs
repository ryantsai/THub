using Microsoft.AspNetCore.Authentication.Negotiate;
using Radzen;
using THub.Application;
using THub.Infrastructure;
using THub.Web.Components;
using THub.Web.Security;

var builder = WebApplication.CreateBuilder(args);

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

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
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
