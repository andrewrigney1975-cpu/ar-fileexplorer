using System.Text.Json;

namespace FileExplorer.Services;

public sealed record AppSettings(
    int ThumbnailSize,
    bool EnableTerminal,
    bool EnableSyncTasks,
    bool EnableFolderWatching,
    bool EnableScripting);

/// App-local store of user preferences (thumbnail bitmap size, feature toggles for the Terminal /
/// Sync Tasks / Folder Watching / Scripting). Everything that reads a setting should read
/// SettingsService.Current live rather than caching it, since it can change at runtime.
public static class SettingsService
{
    public static readonly AppSettings Defaults = new(
        ThumbnailSize: 192,
        EnableTerminal: true,
        EnableSyncTasks: true,
        EnableFolderWatching: true,
        EnableScripting: true);

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorerApp", "settings.json");

    private static AppSettings? _current;

    /// Raised whenever settings are updated, so open UI (toolbar buttons, context menus, panes,
    /// the Control Centre itself) can re-evaluate what should be visible/enabled.
    public static event EventHandler? Changed;

    public static AppSettings Current => _current ??= Load();

    public static void Update(AppSettings settings)
    {
        _current = settings;
        Save(settings);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return Defaults;
    }

    private static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
