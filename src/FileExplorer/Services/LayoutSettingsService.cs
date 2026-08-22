namespace FileExplorer.Services;

public sealed record LayoutState(double? PreviewWidth, bool TerminalOpen, bool PreviewOpen = true);

/// Persists simple window-layout preferences (preview pane width/open state, terminal drawer
/// open/closed) between sessions.
public static class LayoutSettingsService
{
    private static readonly JsonFileStore<LayoutState> Store = new("layout.json", () => new LayoutState(null, false));

    public static LayoutState Load() => Store.Load();

    public static void Save(LayoutState state) => Store.Save(state);
}
