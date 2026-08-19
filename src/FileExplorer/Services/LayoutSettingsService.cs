using System.Text.Json;

namespace FileExplorer.Services;

public sealed record LayoutState(double? PreviewWidth, bool TerminalOpen);

/// Persists simple window-layout preferences (preview pane width, terminal drawer open/closed) between sessions.
public static class LayoutSettingsService
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorerApp", "layout.json");

    public static LayoutState Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new LayoutState(null, false);
            }

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<LayoutState>(json) ?? new LayoutState(null, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new LayoutState(null, false);
        }
    }

    public static void Save(LayoutState state)
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(state));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
