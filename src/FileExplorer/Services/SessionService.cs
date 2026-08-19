using System.Text.Json;

namespace FileExplorer.Services;

public sealed record TabState(string LeftPath, string RightPath, string? Name = null);

/// Persists which tabs (and each pane's folder) were open, so the next launch can restore them.
public static class SessionService
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorerApp", "session.json");

    public static List<TabState> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new List<TabState>();
            }

            var json = File.ReadAllText(FilePath);
            var tabs = JsonSerializer.Deserialize<List<TabState>>(json) ?? new List<TabState>();
            return tabs.Where(t => Directory.Exists(t.LeftPath) && Directory.Exists(t.RightPath)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new List<TabState>();
        }
    }

    public static void Save(IEnumerable<TabState> tabs)
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(tabs.ToList()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
