using System.Globalization;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Localization;
using Radzen;
using Serilog;
using THub.Application;
using THub.Application.Auditing;
using THub.Domain.Auditing;
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
    builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
    builder.Services.Configure<RequestLocalizationOptions>(options =>
    {
        var supportedCultures = new[]
        {
            CultureInfo.GetCultureInfo("en"),
            CultureInfo.GetCultureInfo("zh-TW")
        };

        options.DefaultRequestCulture = new RequestCulture("en");
        options.SupportedCultures = supportedCultures;
        options.SupportedUICultures = supportedCultures;
        options.RequestCultureProviders =
        [
            new CookieRequestCultureProvider(),
            new CustomRequestCultureProvider(context =>
            {
                var browserLanguages = context.Request.GetTypedHeaders().AcceptLanguage;
                var preferredLanguage = browserLanguages?
                    .OrderByDescending(language => language.Quality ?? 1)
                    .Select(language => language.Value.Value)
                    .FirstOrDefault();
                var culture = preferredLanguage?
                    .StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true
                    ? "zh-TW"
                    : "en";

                return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(culture));
            })
        ];
    });
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
    builder.Services.AddScoped<IAuditViewerAuthorization, AuditViewerAuthorization>();
    builder.Services.AddRadzenComponents();
    builder.Services.AddWebApplication();
    builder.Services.AddWebInfrastructure(builder.Configuration);
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
    app.UseRequestLocalization();
    app.UseAuthentication();
    app.Use(async (context, next) =>
    {
        var actor = context.User.Identity?.Name;
        using var auditScope = AuditContext.Push(
            string.IsNullOrWhiteSpace(actor) ? AuditActorKind.System : AuditActorKind.User,
            string.IsNullOrWhiteSpace(actor) ? "thub.web" : actor);
        await next(context);
    });
    app.UseAuthorization();
    app.UseAntiforgery();

    app.MapStaticAssets();
    app.MapHealthChecks("/healthz").AllowAnonymous();
    app.MapGet("/culture/set", (HttpContext context, string culture, string? redirectUri) =>
    {
        var selectedCulture = string.Equals(culture, "zh-TW", StringComparison.OrdinalIgnoreCase)
            ? "zh-TW"
            : "en";
        context.Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(selectedCulture)),
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Secure = context.Request.IsHttps
            });

        var localRedirect = !string.IsNullOrWhiteSpace(redirectUri)
            && redirectUri.StartsWith("/", StringComparison.Ordinal)
            && !redirectUri.StartsWith("//", StringComparison.Ordinal)
                ? redirectUri
                : "/";
        return Results.LocalRedirect(localRedirect);
    }).AllowAnonymous();
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
