using System.Reflection;

namespace Chronicle.Plugin.Wikipedia;

internal enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

/// <summary>
/// Self-contained, dependency-free rolling file logger for this plugin.
///
/// Deliberately does NOT use Serilog's static `Log.Logger`, even though Chronicle's host
/// process is Serilog-configured. Chronicle loads each plugin into an isolated
/// `PluginLoadContext` (per PLUGIN_AUTHORING.md); if this plugin carried its own copy of
/// Serilog.dll (a normal PackageReference would do exactly that), `Serilog.Log` inside this
/// assembly would be a DIFFERENT type identity from the host's — the exact same class of bug
/// PLUGIN_AUTHORING.md warns about for Chronicle.Plugins.dll itself ("loaded twice... causing
/// type identity mismatches that silently break the plugin"). Calls would silently vanish
/// into Serilog's default no-op logger instead of the host's configured sinks — worse than no
/// logging at all, because it would look like it's working. Writing directly to our own file
/// sidesteps the whole question and is guaranteed correct regardless of assembly isolation.
///
/// One file per UTC day, rolling automatically at midnight without needing a restart —
/// resolved by date on every write rather than cached, so a long-running process still rolls.
/// </summary>
internal static class PluginLog
{
    private static readonly object Gate = new();
    private static readonly string LogDirectory = ResolveLogDirectory();

    /// <summary>Minimum level that gets written. Configurable via the plugin's "log_level"
    /// setting (Configure() calls SetMinLevel) so verbosity can be turned down without a
    /// rebuild — defaults to Info until Configure() runs, then to whatever was configured.</summary>
    private static LogLevel _minLevel = LogLevel.Info;

    public static void SetMinLevel(LogLevel level) => _minLevel = level;

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message, Exception? ex = null) =>
        Write(LogLevel.Error, ex is null ? message : $"{message} — {ex}");

    private static void Write(LogLevel level, string message)
    {
        if (level < _minLevel) return;

        var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z] [{level,-5}] {message}";

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                var path = Path.Combine(LogDirectory, $"wikipedia-{DateTime.UtcNow:yyyyMMdd}.log");
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // A logging failure (disk full, permissions, locked file) must never take down
                // enrichment itself — this is diagnostics, not a core operation. Deliberately
                // swallowed; there's nowhere safe left to report it to.
            }
        }
    }

    /// <summary>Logs directory sits next to the plugin DLL (Chronicle.API/plugins/
    /// chronicle.plugin.wikipedia/logs/), so it's found the same way a user would look for
    /// any other plugin's files, and gets cleaned up automatically if the plugin is
    /// uninstalled (the whole plugin directory is removed per PLUGIN_AUTHORING.md's
    /// uninstall lifecycle).</summary>
    private static string ResolveLogDirectory()
    {
        var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        return Path.Combine(asmDir ?? AppContext.BaseDirectory, "logs");
    }
}
