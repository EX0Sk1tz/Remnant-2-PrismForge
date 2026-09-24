using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace R2PrismRuntime.Diagnostics;

public enum LogLevel { Trace, Debug, Info, Warn, Error }

/// <summary>
/// Thread-safe file logger. One file per app session under %LocalAppData%\Prismforge\logs;
/// only the newest <see cref="KeepFiles"/> logs and diagnostic reports are kept.
/// Every line is also raised via <see cref="LineWritten"/> so the UI can show a live log.
/// </summary>
public static class Log
{
    private static readonly object _lock = new();
    private static StreamWriter? _writer;
    private const int KeepFiles = 10;

    /// Lines below this level are dropped (file + UI). --verbose lowers it to Debug.
    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    public static string FilePath { get; private set; } = "";
    public static string LogDirectory => AppInfo.LogDirectory;

    /// Raised for every written line (on the calling thread — marshal to UI yourself).
    public static event Action<LogLevel, string>? LineWritten;

    public static void Init()
    {
        lock (_lock)
        {
            if (_writer != null) return;
            try
            {
                Directory.CreateDirectory(LogDirectory);
                Prune("Prismforge_*.log", KeepFiles - 1);
                Prune("Prismforge_Diag_*.txt", KeepFiles);
                FilePath = Path.Combine(LogDirectory, $"Prismforge_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                _writer = new StreamWriter(FilePath, append: false, Encoding.UTF8) { AutoFlush = true };
            }
            catch
            {
                // Logging must never take the app down; fall back to UI-only logging.
                _writer = null;
            }
        }
        Info($"{AppInfo.Name} {AppInfo.Version}. .NET {Environment.Version}, OS {Environment.OSVersion}, 64-bit process: {Environment.Is64BitProcess}");
        Debug($"Log file: {FilePath}");   // Debug: keeps the user's profile path out of the Journal panel
    }

    /// Deletes all but the newest <paramref name="keep"/> files matching <paramref name="pattern"/>.
    private static void Prune(string pattern, int keep)
    {
        foreach (var f in new DirectoryInfo(LogDirectory).GetFiles(pattern)
                     .OrderByDescending(f => f.LastWriteTimeUtc).Skip(Math.Max(0, keep)))
            try { f.Delete(); } catch { /* in use or locked — try next time */ }
    }

    public static void Shutdown()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    public static void Trace(string msg, [CallerMemberName] string caller = "") => Write(LogLevel.Trace, msg, caller);
    public static void Debug(string msg, [CallerMemberName] string caller = "") => Write(LogLevel.Debug, msg, caller);
    public static void Info (string msg, [CallerMemberName] string caller = "") => Write(LogLevel.Info,  msg, caller);
    public static void Warn (string msg, [CallerMemberName] string caller = "") => Write(LogLevel.Warn,  msg, caller);
    public static void Error(string msg, [CallerMemberName] string caller = "") => Write(LogLevel.Error, msg, caller);

    public static void Error(string msg, Exception ex, [CallerMemberName] string caller = "")
        => Write(LogLevel.Error, $"{msg}\n{ex}", caller);

    private static void Write(LogLevel level, string msg, string caller)
    {
        if (level < MinLevel) return;
        string line = $"{DateTime.Now:HH:mm:ss.fff} [{level,-5}] [T{Environment.CurrentManagedThreadId:D2}] {caller}: {msg}";
        lock (_lock)
        {
            try { _writer?.WriteLine(line); } catch { /* ignore */ }
        }
        System.Diagnostics.Debug.WriteLine(line);
        try { LineWritten?.Invoke(level, line); } catch { /* never let a subscriber break logging */ }
    }

    /// Hex helper used all over the diagnostics.
    public static string Hex(ulong v) => $"0x{v:X}";
}
