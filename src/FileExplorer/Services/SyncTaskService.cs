using System.Text.Json;
using FileExplorer.Models;

namespace FileExplorer.Services;

/// IncludeHiddenSystemFiles defaults to false: a sync run skips hidden/system files and folders on
/// the source side entirely rather than mirroring them, matching most users' expectation that a
/// folder sync means "my visible files," not OS/app metadata like Thumbs.db or desktop.ini.
public sealed record SyncTaskState(string Id, string Name, string SourcePath, string TargetPath, bool IncludeHiddenSystemFiles = false);

/// App-local store of folder-sync task pairings, plus the transient (never persisted) in-progress
/// "set source, then set target" selection state used while the user is defining a new one.
public static class SyncTaskService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorerApp", "sync-tasks.json");

    private static List<SyncTaskState>? _cache;

    /// Raised whenever pending selection state changes or a task is added/removed, so panes can
    /// re-render their highlight bars and the toolbar can refresh its task list.
    public static event EventHandler? Changed;

    public static string? PendingSourcePath { get; private set; }

    public static string? PendingTargetPath { get; private set; }

    public static IReadOnlyList<SyncTaskState> Tasks => LoadCache();

    public static void SetPendingSource(string path)
    {
        PendingSourcePath = path;
        PendingTargetPath = null;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void SetPendingTarget(string path)
    {
        PendingTargetPath = path;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void ClearPending()
    {
        if (PendingSourcePath is null && PendingTargetPath is null)
        {
            return;
        }

        PendingSourcePath = null;
        PendingTargetPath = null;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static SyncTaskState AddTask(string name, string sourcePath, string targetPath, bool includeHiddenSystemFiles = false)
    {
        var task = new SyncTaskState(Guid.NewGuid().ToString(), name, sourcePath, targetPath, includeHiddenSystemFiles);
        var list = LoadCache();
        list.Add(task);
        Save(list);
        Changed?.Invoke(null, EventArgs.Empty);
        return task;
    }

    /// Sync tasks are referenced elsewhere (ScheduleService) by Id, not Name, so renaming here
    /// never breaks a scheduled or watch-triggered run - it's a pure display-name change.
    public static bool RenameTask(string id, string newName)
    {
        var list = LoadCache();
        var index = list.FindIndex(t => t.Id == id);
        if (index < 0)
        {
            return false;
        }

        list[index] = list[index] with { Name = newName };
        Save(list);
        Changed?.Invoke(null, EventArgs.Empty);
        return true;
    }

    public static void RemoveTask(string id)
    {
        var list = LoadCache();
        if (list.RemoveAll(t => t.Id == id) > 0)
        {
            Save(list);
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    /// Whether path is a saved task's source, a saved task's target, or (taking priority) the
    /// in-progress pending source/target - so the highlight bar appears immediately during setup.
    public static SyncRole GetRole(string path)
    {
        if (PendingSourcePath is { } pendingSource && string.Equals(pendingSource, path, StringComparison.OrdinalIgnoreCase))
        {
            return SyncRole.Source;
        }

        if (PendingTargetPath is { } pendingTarget && string.Equals(pendingTarget, path, StringComparison.OrdinalIgnoreCase))
        {
            return SyncRole.Target;
        }

        var list = LoadCache();

        if (list.Any(t => string.Equals(t.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
        {
            return SyncRole.Source;
        }

        if (list.Any(t => string.Equals(t.TargetPath, path, StringComparison.OrdinalIgnoreCase)))
        {
            return SyncRole.Target;
        }

        return SyncRole.None;
    }

    private static List<SyncTaskState> LoadCache()
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
                _cache = JsonSerializer.Deserialize<List<SyncTaskState>>(json) ?? new List<SyncTaskState>();
                return _cache;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        _cache = new List<SyncTaskState>();
        return _cache;
    }

    private static void Save(List<SyncTaskState> list)
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
