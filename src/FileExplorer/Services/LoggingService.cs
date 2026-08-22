namespace FileExplorer.Services;

public enum LogLevel
{
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

    public static void LogWarning(string source, Exception ex) => Write(LogLevel.Warning, source, ex);

    public static void LogError(string source, Exception ex) => Write(LogLevel.Error, source, ex);

    private static void Write(LogLevel level, string source, Exception ex)
    {
        lock (Lock)
        {
            try
            {
                File.AppendAllText(FilePath, $"[{DateTime.Now:O}] {level} {source}\n{ex}\n\n");
            }
            catch
            {
                // best effort
            }
        }
    }
}
