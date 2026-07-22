using Serilog;
using Serilog.Formatting.Json;

namespace THub.Worker;

internal static class SerilogConfiguration
{
    public static void Configure(HostApplicationBuilder builder)
    {
        builder.Services.AddSerilog((services, configuration) => configuration
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "THub.Worker")
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

    private static string ResolveLogPath(HostApplicationBuilder builder)
    {
        var configuredPath = builder.Configuration["Serilog:FilePath"]
            ?? Path.Combine("logs", "thub-worker-.json");
        var expandedPath = Environment.ExpandEnvironmentVariables(configuredPath);
        return Path.IsPathRooted(expandedPath)
            ? expandedPath
            : Path.GetFullPath(expandedPath, builder.Environment.ContentRootPath);
    }
}
