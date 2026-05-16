using InfiniContext.Core.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace InfiniContext.Core.Logging;

/// <summary>
/// One log file per host run, written to <c>{logDirectory}/{YYYYMMDD-HHmmss}.log</c>. Configures the
/// scrubber, the per-event output template, and a default <c>SessionContext</c> property so logs
/// emitted before any <see cref="InfiniContext.Core.Sessions.ClaudeSession"/> exists still render cleanly.
///
/// The output template is the design doc §7.4 format:
/// <c>2026-05-06 14:30:52.123 [INF] [Session a4b4-ce91 | cwd /Users/x/proj] state Idle → Sending; …</c>
/// — the bracketed prefix is provided by callers via the <c>SessionContext</c> property; absent that,
/// it renders as the empty string.
/// </summary>
public static class InfiniContextLogger
{
    public const string SessionContextPropertyName = "SessionContext";

    /// <summary>
    /// Output template. <c>SessionContext</c> is rendered literally (<c>:l</c>), so callers control the
    /// brackets/spacing; the message itself does not need to repeat the session label.
    /// </summary>
    public const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SessionContext:l}{Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Initialise a per-run logger that writes to <c>{logDirectory ?? ./logs}/{timestamp}.log</c>.
    /// Caller owns the returned handle and must dispose it on host shutdown so the file sink flushes.
    /// </summary>
    public static LoggingHandle ConfigureFromHostStartup(
        string? logDirectory = null,
        LogEventLevel minimumLevel = LogEventLevel.Debug,
        int maxFieldBytes = SecretScrubbingEnricher.DefaultMaxFieldBytes,
        DateTimeOffset? clock = null,
        string? prefix = null,
        int flushIntervalMs = 250)
    {
        var ts = (clock ?? DateTimeOffset.Now).ToString("yyyyMMdd-HHmmss");
        var dir = Path.GetFullPath(logDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "logs"));
        Directory.CreateDirectory(dir);

        var stem = string.IsNullOrEmpty(prefix) ? ts : $"{prefix}-{ts}";
        var path = Path.Combine(dir, $"{stem}.log");
        var n = 1;
        while (File.Exists(path)) path = Path.Combine(dir, $"{stem}-{n++}.log");

        var logger = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .Enrich.WithProperty(SessionContextPropertyName, "")
            .Enrich.With(new SecretScrubbingEnricher(maxFieldBytes))
            .WriteTo.File(
                path: path,
                outputTemplate: OutputTemplate,
                shared: false,
                buffered: false,
                rollingInterval: RollingInterval.Infinite,
                flushToDiskInterval: TimeSpan.FromMilliseconds(flushIntervalMs))
            .CreateLogger();

        return new LoggingHandle(logger, path);
    }

    /// <summary>
    /// Same as the parameter-list overload, but resolves all knobs from <paramref name="options"/>.
    /// Production callers prefer this; tests still use the parameter overload directly.
    /// </summary>
    public static LoggingHandle ConfigureFromHostStartup(
        LoggingOptions options,
        DateTimeOffset? clock = null,
        string? prefix = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var level = Enum.TryParse<LogEventLevel>(options.Level, ignoreCase: true, out var parsed)
            ? parsed
            : LogEventLevel.Debug;
        return ConfigureFromHostStartup(
            logDirectory: options.Directory,
            minimumLevel: level,
            maxFieldBytes: options.MaxFieldBytes,
            clock: clock,
            prefix: prefix,
            flushIntervalMs: options.FlushIntervalMs);
    }

    /// <summary>
    /// Build the bracketed session label used as the <c>SessionContext</c> property. Pre-init the session
    /// id is unknown — pass null to get <c>[Session new | cwd …]</c>.
    /// </summary>
    public static string FormatSessionContext(Guid? sessionId, string cwd)
    {
        var id = sessionId is { } g ? ShortenGuid(g) : "new";
        return $"[Session {id} | cwd {cwd}] ";
    }

    private static string ShortenGuid(Guid g)
    {
        // First two segments (xxxxxxxx-xxxx) are enough to disambiguate across a small N of sessions and
        // match the design doc's example formatting.
        var s = g.ToString();
        var dash = s.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0 || dash + 5 > s.Length) return s;
        return s.Substring(0, dash + 5);
    }
}

/// <summary>Disposable wrapper exposing both the configured logger and the file path it writes to.</summary>
public sealed class LoggingHandle : IDisposable, IAsyncDisposable
{
    public LoggingHandle(Logger logger, string logFilePath)
    {
        Logger = logger;
        LogFilePath = logFilePath;
    }

    public Logger Logger { get; }
    public string LogFilePath { get; }

    public void Dispose() => Logger.Dispose();
    public ValueTask DisposeAsync() { Logger.Dispose(); return ValueTask.CompletedTask; }
}
