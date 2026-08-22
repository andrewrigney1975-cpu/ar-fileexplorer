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

    private static readonly JsonFileStore<List<WatchTaskState>> Store = new("watches.json", () => new List<WatchTaskState>());

    private static readonly Dictionary<string, FileSystemWatcher> _watchers = new();
    private static readonly Dictionary<string, Timer> _debounceTimers = new();
    private static readonly Dictionary<string, List<string>> _pendingPaths = new();
    private static readonly object _lock = new();

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

    /// Disables the live FileSystemWatcher for one task while its script runs, so the script's own
    /// writes into the watched folder (e.g. a rename-in-place) can't re-trigger the same watch and
    /// loop forever. Call before running a triggered script and ResumeWatcher in a finally block.
    public static void PauseWatcher(string id)
    {
        lock (_lock)
        {
            if (_watchers.TryGetValue(id, out var watcher))
            {
                watcher.EnableRaisingEvents = false;
            }
        }
    }

    public static void ResumeWatcher(string id)
    {
        lock (_lock)
        {
            if (_watchers.TryGetValue(id, out var watcher))
            {
                watcher.EnableRaisingEvents = true;
            }
        }
    }

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
                // FileName and DirectoryName are separate Win32 notification classes -
                // FileName alone (the original setting here) never raises Created/Renamed for a
                // folder at all, only for files. Verified empirically: a plain subfolder create,
                // and a same-volume Directory.Move into the watched folder, both produced zero
                // events with FileName alone, and both fired Created once DirectoryName was added.
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true,
            };

            // A same-volume move fires Created when the source was outside the watched folder
            // (confirmed above), but renaming an item already inside it fires Renamed instead -
            // wire both to the same handler so neither case is missed.
            watcher.Created += (_, e) => OnCreated(task, e.FullPath);
            watcher.Renamed += (_, e) => OnCreated(task, e.FullPath);
            _watchers[task.Id] = watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The watch task still shows as saved in the UI but silently never triggers - no other
            // signal exists that it failed to actually start.
            LoggingService.LogWarning($"WatchService.StartWatcher: {task.FolderPath}", ex);
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

    private static List<WatchTaskState> LoadCache() => Store.Load();

    private static void Save(List<WatchTaskState> list) => Store.Save(list);
}
