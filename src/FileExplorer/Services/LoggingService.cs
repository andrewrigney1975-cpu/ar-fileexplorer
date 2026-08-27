namespace FileExplorer.Services;

public enum LogLevel
{
    Info,
    Warning,
    Error,
}

/// Shared structured logging sink for exceptions caught (and, before this existed, silently
/// swallowed) throughout the app. Writes one leveled, sourced entry per call to app.log next to
/// the exe - alongside crash.log (fatal/unhandled exceptions only, written directly by
/// App.xaml.cs and left untouched by this) and the now-migrated thumbnail-errors.log.
public static class LoggingService
{
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "app.log");
    private static readonly object Lock = new();

    public static void LogWarning(string source, Exception ex) => Write(LogLevel.Warning, source, ex.ToString());

    public static void LogError(string source, Exception ex) => Write(LogLevel.Error, source, ex.ToString());

    /// Lightweight checkpoint/trace logging - no Exception needed, just a message. Added for
    /// instrumenting a reported "operation appears to hang" bug where the failure point wasn't
    /// obvious from exception logging alone; keep using it for that kind of tracing rather than only
    /// exception paths.
    public static void LogInfo(string source, string message) => Write(LogLevel.Info, source, message);

    private static void Write(LogLevel level, string source, string message)
    {
        lock (Lock)
        {
            try
            {
                File.AppendAllText(FilePath, $"[{DateTime.Now:O}] {level} {source}\n{message}\n\n");
            }
            catch
            {
                // best effort
            }
        }
    }
}
