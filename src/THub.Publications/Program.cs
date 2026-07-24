using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.OpenApi;
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
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "THub Published Data API",
            Version = "v1",
            Description =
                "Read-only access to reviewed THub data publications. Use the publication slug " +
                "and the opaque bearer token supplied by the publication owner."
        });
        options.AddSecurityDefinition("publicationBearer", new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "THub opaque token",
            Description =
                "Enter the one-time THub publication token. Swagger keeps it only for this page session."
        });
        options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("publicationBearer", document)] = []
        });
        options.OperationFilter<PublicationRowsQueryOperationFilter>();
    });
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
    app.UseSwagger(options =>
    {
        options.RouteTemplate = "openapi/{documentName}.json";
    });
    app.UseSwaggerUI(options =>
    {
        options.RoutePrefix = "swagger";
        options.SwaggerEndpoint("/openapi/v1.json", "THub Published Data API v1");
        options.DocumentTitle = "THub Published Data API";
        options.DisplayRequestDuration();
        options.EnableTryItOutByDefault();
        options.SupportedSubmitMethods(Swashbuckle.AspNetCore.SwaggerUI.SubmitMethod.Get);
    });
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
