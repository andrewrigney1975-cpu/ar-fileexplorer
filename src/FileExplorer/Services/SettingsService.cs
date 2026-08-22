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

    private static readonly JsonFileStore<AppSettings> Store = new("settings.json", () => Defaults);

    /// Raised whenever settings are updated, so open UI (toolbar buttons, context menus, panes,
    /// the Control Centre itself) can re-evaluate what should be visible/enabled.
    public static event EventHandler? Changed;

    public static AppSettings Current => Store.Load();

    public static void Update(AppSettings settings)
    {
        Store.Save(settings);
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
