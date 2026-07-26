using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Conduit.Core.Logging;

/// <summary>
/// Central logging entry point. Writes structured, rolling log files that you can inspect
/// to diagnose runtime bugs and connection issues, plus console output during development.
///
/// Logs land in:  %LOCALAPPDATA%\Conduit\logs\conduit-&lt;date&gt;.log
/// </summary>
public static class ConduitLog
{
    private static readonly LoggingLevelSwitch LevelSwitch = new(LogEventLevel.Debug);

    /// <summary>Directory where log files are written.</summary>
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Conduit", "logs");

    /// <summary>Call once at app startup.</summary>
    public static void Initialize()
    {
        Directory.CreateDirectory(LogDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(LevelSwitch)
            .Enrich.FromLogContext()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Tag} {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: Path.Combine(LogDirectory, "conduit-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 20 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Tag} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        For("Startup").Information("Conduit logging initialized. Log dir: {Dir}", LogDirectory);
    }

    /// <summary>Get a logger scoped to a subsystem tag, e.g. For("Discovery").</summary>
    public static ILogger For(string tag) => Log.Logger.ForContext("Tag", $"[{tag}]");

    /// <summary>Adjust verbosity at runtime (from settings UI).</summary>
    public static void SetLevel(LogEventLevel level)
    {
        LevelSwitch.MinimumLevel = level;
        For("Logging").Information("Log level set to {Level}", level);
    }

    public static void Shutdown() => Log.CloseAndFlush();
}
