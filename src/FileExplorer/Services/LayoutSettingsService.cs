using System.Text.Json;

namespace FileExplorer.Services;

/// Persists simple window-layout preferences (currently just the preview pane's width) between sessions.
public static class LayoutSettingsService
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorerApp", "layout.json");

    private sealed record LayoutState(double PreviewWidth);

    public static double? LoadPreviewWidth()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<LayoutState>(json)?.PreviewWidth;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void SavePreviewWidth(double width)
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new LayoutState(width)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
