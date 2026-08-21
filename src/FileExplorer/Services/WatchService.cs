using System.Text.Json;

namespace FileExplorer.Services;

public sealed record WatchTaskState(string Id, string FolderPath, string ScriptName);

public sealed class WatchTriggeredEventArgs : EventArgs
{
    public required WatchTaskState Task { get; init; }
    public required IReadOnlyList<string> AddedPaths { get; init; }
}

/// App-local store of "run this script when files are added to this folder" bindings. Each saved
/// task gets a live FileSystemWatcher; rapid bursts of file creation (e.g. a batch copy landing in
/// the folder) are debounced into a single Triggered event rather than one per file.
public static class WatchService
{
    private const int DebounceMilliseconds = 750;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorerApp", "watches.json");

    private static readonly Dictionary<string, FileSystemWatcher> _watchers = new();
    private static readonly Dictionary<string, Timer> _debounceTimers = new();
    private static readonly Dictionary<string, List<string>> _pendingPaths = new();
    private static readonly object _lock = new();

    private static List<WatchTaskState>? _cache;

    /// Raised whenever a watch task is added or removed, so panes can re-render their highlight bars.
    public static event EventHandler? Changed;

    /// Raised (off the UI thread) once a debounce window closes with at least one newly-added file.
    public static event EventHandler<WatchTriggeredEventArgs>? Triggered;

    public static IReadOnlyList<WatchTaskState> Tasks => LoadCache();

    /// Starts a FileSystemWatcher for every persisted task. Call once at app startup.
    public static void Start()
    {
        lock (_lock)
        {
            foreach (var task in LoadCache())
            {
                StartWatcher(task);
            }
        }
    }

    public static WatchTaskState AddTask(string folderPath, string scriptName)
    {
        var task = new WatchTaskState(Guid.NewGuid().ToString(), folderPath, scriptName);
        var list = LoadCache();
        list.Add(task);
        Save(list);

        lock (_lock)
        {
            StartWatcher(task);
        }

        Changed?.Invoke(null, EventArgs.Empty);
        return task;
    }

    public static void RemoveTask(string id)
    {
        var list = LoadCache();
        var removed = list.FirstOrDefault(t => t.Id == id);
        if (removed is null)
        {
            return;
        }

        list.Remove(removed);
        Save(list);

        lock (_lock)
        {
            StopWatcher(id);
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// Repoints every watch task bound to oldName at newName, so a renamed script keeps triggering
    /// from its watched folder. Call together with ScriptService.Rename. Restarts each affected
    /// watcher - its FileSystemWatcher.Created handler closes over the WatchTaskState captured at
    /// StartWatcher time, so without this it would go on triggering the old (now-renamed-away)
    /// script name until the app restarts.
    public static void RenameScriptReferences(string oldName, string newName)
    {
        var list = LoadCache();
        var affectedIds = new List<string>();

        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].ScriptName, oldName, StringComparison.OrdinalIgnoreCase))
            {
                list[i] = list[i] with { ScriptName = newName };
                affectedIds.Add(list[i].Id);
            }
        }

        if (affectedIds.Count == 0)
        {
            return;
        }

        Save(list);

        lock (_lock)
        {
            foreach (var id in affectedIds)
            {
                StopWatcher(id);
                var task = list.First(t => t.Id == id);
                StartWatcher(task);
            }
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static bool IsWatched(string path) =>
        LoadCache().Any(t => string.Equals(t.FolderPath, path, StringComparison.OrdinalIgnoreCase));

    private static void StartWatcher(WatchTaskState task)
    {
        if (_watchers.ContainsKey(task.Id) || !Directory.Exists(task.FolderPath))
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(task.FolderPath)
            {
                NotifyFilter = NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };

            watcher.Created += (_, e) => OnCreated(task, e.FullPath);
            _watchers[task.Id] = watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    private static void StopWatcher(string id)
    {
        if (_watchers.Remove(id, out var watcher))
        {
            watcher.Dispose();
        }

        if (_debounceTimers.Remove(id, out var timer))
        {
            timer.Dispose();
        }

        _pendingPaths.Remove(id);
    }

    private static void OnCreated(WatchTaskState task, string fullPath)
    {
        lock (_lock)
        {
            if (!_pendingPaths.TryGetValue(task.Id, out var paths))
            {
                paths = new List<string>();
                _pendingPaths[task.Id] = paths;
            }

            paths.Add(fullPath);

            if (_debounceTimers.TryGetValue(task.Id, out var existing))
            {
                existing.Change(DebounceMilliseconds, Timeout.Infinite);
                return;
            }

            _debounceTimers[task.Id] = new Timer(_ => Flush(task), null, DebounceMilliseconds, Timeout.Infinite);
        }
    }

    private static void Flush(WatchTaskState task)
    {
        List<string> paths;
        lock (_lock)
        {
            if (!_pendingPaths.Remove(task.Id, out paths!) || paths.Count == 0)
            {
                return;
            }

            if (_debounceTimers.Remove(task.Id, out var timer))
            {
                timer.Dispose();
            }
        }

        Triggered?.Invoke(null, new WatchTriggeredEventArgs { Task = task, AddedPaths = paths });
    }

    private static List<WatchTaskState> LoadCache()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                _cache = JsonSerializer.Deserialize<List<WatchTaskState>>(json) ?? new List<WatchTaskState>();
                return _cache;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        _cache = new List<WatchTaskState>();
        return _cache;
    }

    private static void Save(List<WatchTaskState> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
