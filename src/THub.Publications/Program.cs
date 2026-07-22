using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Serilog;
using THub.Application;
using THub.Infrastructure;
using THub.Publications;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    SerilogConfiguration.Configure(builder);
    builder.Services.AddProblemDetails();
    builder.Services.AddHealthChecks();
    builder.Services.AddPublicationApiApplication();
    builder.Services.AddPublicationApiInfrastructure(builder.Configuration);
    builder.Services.AddSingleton<PublicationAdmissionGate>();

    var app = builder.Build();
    app.UseExceptionHandler();
    if (!app.Environment.IsDevelopment())
    {
        app.UseHsts();
    }

    app.UseHttpsRedirection();
    app.UseSerilogRequestLogging();
    app.MapHealthChecks("/healthz", new HealthCheckOptions
    {
        AllowCachingResponses = false
    });
    app.MapPublicationApi();

    app.Run();
}
catch (HostAbortedException)
{
    throw;
}
catch (Exception exception)
{
    Log.Fatal(exception, "THub.Publications terminated unexpectedly.");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program;
