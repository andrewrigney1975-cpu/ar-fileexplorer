using System.Text.Json;

namespace FileExplorer.Services;

public sealed record TabState(string LeftPath, string RightPath, string? Name = null);

public interface ISessionService
{
    List<TabState> Load();
    void Save(IEnumerable<TabState> tabs);
}

/// Persists which tabs (and each pane's folder) were open, so the next launch can restore them.
public sealed class SessionService : ISessionService
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorerApp", "session.json");

    public List<TabState> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new List<TabState>();
            }

            var json = File.ReadAllText(FilePath);
            var tabs = JsonSerializer.Deserialize<List<TabState>>(json) ?? new List<TabState>();

            // Remote pane locations are never restored across restarts (deliberate v1 scope cut -
            // reconnecting would mean silently blocking startup on network I/O and possibly a
            // password prompt). ExistsLocally returns false for a remote path, which drops the
            // whole tab exactly like a since-deleted local folder would.
            return tabs.Where(t => ExistsLocally(t.LeftPath) && ExistsLocally(t.RightPath)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new List<TabState>();
        }
    }

    private static bool ExistsLocally(string path) => !RemotePathService.IsRemote(path) && Directory.Exists(path);

    public void Save(IEnumerable<TabState> tabs)
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
