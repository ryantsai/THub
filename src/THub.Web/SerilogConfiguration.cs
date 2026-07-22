using Serilog;
using Serilog.Formatting.Json;

namespace THub.Web;

internal static class SerilogConfiguration
{
    public static void Configure(WebApplicationBuilder builder)
    {
        builder.Services.AddSerilog((services, configuration) => configuration
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "THub.Web")
            .WriteTo.Console()
            .WriteTo.File(
                new JsonFormatter(renderMessage: true),
                ResolveLogPath(builder),
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: 50 * 1024 * 1024,
                retainedFileCountLimit: 14,
                shared: false,
                flushToDiskInterval: TimeSpan.FromSeconds(1)));
    }

    private static string ResolveLogPath(WebApplicationBuilder builder)
    {
        var configuredPath = builder.Configuration["Serilog:FilePath"]
            ?? Path.Combine("logs", "thub-web-.json");
        var expandedPath = Environment.ExpandEnvironmentVariables(configuredPath);
        return Path.IsPathRooted(expandedPath)
            ? expandedPath
            : Path.GetFullPath(expandedPath, builder.Environment.ContentRootPath);
    }
}
