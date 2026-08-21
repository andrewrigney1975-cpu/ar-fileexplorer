using System.Text.Json;

namespace FileExplorer.Services;

public enum ScheduleKind
{
    Script,
    Sync,
}

/// TargetName holds a script name (ScriptService key) when Kind == Script, or a sync task's Id
/// (SyncTaskState.Id) when Kind == Sync - an Id rather than a name there since sync task names
/// aren't guaranteed unique.
public sealed record ScheduleState(string Id, ScheduleKind Kind, string TargetName, int IntervalMinutes, DateTimeOffset NextRunUtc);

/// App-local store of interval-based schedules that re-run a saved script or a saved sync task on
/// a fixed cadence, checked by a single background timer against each schedule's NextRunUtc.
public static class ScheduleService
{
    private const int PollIntervalSeconds = 30;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorerApp", "schedules.json");

    private static readonly object _lock = new();
    private static List<ScheduleState>? _cache;
    private static Timer? _pollTimer;

    /// Raised whenever a schedule is added or removed, so the manager UI can refresh its list.
    public static event EventHandler? Changed;

    /// Raised (off the UI thread) each time a schedule comes due; the handler is responsible for
    /// actually running the script/sync task.
    public static event EventHandler<ScheduleState>? Due;

    public static IReadOnlyList<ScheduleState> Schedules => LoadCache();

    /// Starts the polling timer. Call once at app startup.
    public static void Start()
    {
        _pollTimer ??= new Timer(_ => Poll(), null, TimeSpan.FromSeconds(PollIntervalSeconds), TimeSpan.FromSeconds(PollIntervalSeconds));
    }

    public static ScheduleState AddSchedule(ScheduleKind kind, string targetName, int intervalMinutes)
    {
        var schedule = new ScheduleState(
            Guid.NewGuid().ToString(), kind, targetName, intervalMinutes,
            DateTimeOffset.UtcNow.AddMinutes(intervalMinutes));

        var list = LoadCache();
        list.Add(schedule);
        Save(list);
        Changed?.Invoke(null, EventArgs.Empty);
        return schedule;
    }

    /// Repoints every Script-kind schedule bound to oldName at newName, so a renamed script keeps
    /// running on its interval. Call together with ScriptService.Rename. (Sync-kind schedules use
    /// SyncTaskState.Id as TargetName, which doesn't change on a sync task rename, so they never
    /// need this.)
    public static void RenameScriptTarget(string oldName, string newName)
    {
        var list = LoadCache();
        var changed = false;

        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Kind == ScheduleKind.Script && string.Equals(list[i].TargetName, oldName, StringComparison.OrdinalIgnoreCase))
            {
                list[i] = list[i] with { TargetName = newName };
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        Save(list);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void RemoveSchedule(string id)
    {
        var list = LoadCache();
        if (list.RemoveAll(s => s.Id == id) > 0)
        {
            Save(list);
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    private static void Poll()
    {
        List<ScheduleState> due;
        lock (_lock)
        {
            var list = LoadCache();
            var now = DateTimeOffset.UtcNow;
            due = list.Where(s => s.NextRunUtc <= now).ToList();

            if (due.Count == 0)
            {
                return;
            }

            for (var i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (due.Any(d => d.Id == s.Id))
                {
                    list[i] = s with { NextRunUtc = DateTimeOffset.UtcNow.AddMinutes(s.IntervalMinutes) };
                }
            }

            Save(list);
        }

        foreach (var schedule in due)
        {
            Due?.Invoke(null, schedule);
        }
    }

    private static List<ScheduleState> LoadCache()
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
                _cache = JsonSerializer.Deserialize<List<ScheduleState>>(json) ?? new List<ScheduleState>();
                return _cache;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        _cache = new List<ScheduleState>();
        return _cache;
    }

    private static void Save(List<ScheduleState> list)
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
