using Serilog;
using Serilog.Events;

namespace WorkToUnlock.Infrastructure.Logging;

/// <summary>Serilog configuration shared by the agent, the dashboard and the service.</summary>
public static class LoggingSetup
{
    public static ILogger CreateLogger(string logsDirectory, string componentName, bool verbose = false)
    {
        Directory.CreateDirectory(logsDirectory);
        return new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Component", componentName)
            .WriteTo.File(
                Path.Combine(logsDirectory, $"{componentName.ToLowerInvariant()}-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
