namespace FileExplorer.Services;

public sealed record TabState(string LeftPath, string RightPath, string? Name = null, string? Icon = null);

public interface ISessionService
{
    List<TabState> Load();
    void Save(IEnumerable<TabState> tabs);
}

/// Persists which tabs (and each pane's folder) were open, so the next launch can restore them.
public sealed class SessionService : ISessionService
{
    private readonly JsonFileStore<List<TabState>> _store = new("session.json", () => new List<TabState>());

    public List<TabState> Load()
    {
        // Remote pane locations are never restored across restarts (deliberate v1 scope cut -
        // reconnecting would mean silently blocking startup on network I/O and possibly a
        // password prompt). ExistsLocally returns false for a remote path, which drops the
        // whole tab exactly like a since-deleted local folder would.
        return _store.Load().Where(t => ExistsLocally(t.LeftPath) && ExistsLocally(t.RightPath)).ToList();
    }

    private static bool ExistsLocally(string path) => !RemotePathService.IsRemote(path) && Directory.Exists(path);

    public void Save(IEnumerable<TabState> tabs) => _store.Save(tabs.ToList());
}
