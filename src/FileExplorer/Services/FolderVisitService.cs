namespace FileExplorer.Services;

public sealed record FolderVisitRecord(string Path, int VisitCount, DateTimeOffset LastVisitedUtc);

/// Tracks how often (and how recently) the user navigates into each local folder, purely to decide
/// which folders are worth pre-warming into FileSystemService's listing cache on startup - see
/// MainWindow.PrewarmFrequentFoldersAsync. This is not a "recent folders" UI feature and nothing
/// user-visible reads it.
public static class FolderVisitService
{
    private const int MaxTrackedFolders = 200;

    private static readonly JsonFileStore<List<FolderVisitRecord>> Store = new("folder-visits.json", () => new List<FolderVisitRecord>());

    public static void RecordVisit(string path)
    {
        var list = Store.Load();
        var index = list.FindIndex(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
        {
            list[index] = list[index] with { VisitCount = list[index].VisitCount + 1, LastVisitedUtc = DateTimeOffset.UtcNow };
        }
        else
        {
            list.Add(new FolderVisitRecord(path, 1, DateTimeOffset.UtcNow));
        }

        // Keeps this file from growing unbounded for a long-lived install that's visited thousands
        // of distinct folders over time - trims to the MaxTrackedFolders most relevant by the same
        // frequency/recency score GetTopFolders ranks by, not just insertion order.
        if (list.Count > MaxTrackedFolders)
        {
            list = list.OrderByDescending(Score).Take(MaxTrackedFolders).ToList();
        }

        Store.Save(list);
    }

    /// Highest-scoring folders (frequency, recency-weighted), most relevant first. Callers must
    /// still check Directory.Exists themselves - a tracked folder can be deleted, or live on a
    /// removable/network drive that isn't currently attached.
    public static List<string> GetTopFolders(int count) =>
        Store.Load()
            .OrderByDescending(Score)
            .Take(count)
            .Select(r => r.Path)
            .ToList();

    // Blends raw visit count with recency so a folder hit heavily months ago doesn't permanently
    // outrank one the user has actually been living in this week - halves in weight every 14 days.
    private static double Score(FolderVisitRecord record)
    {
        var ageDays = Math.Max(0, (DateTimeOffset.UtcNow - record.LastVisitedUtc).TotalDays);
        var recencyWeight = Math.Pow(0.5, ageDays / 14.0);
        return record.VisitCount * recencyWeight;
    }
}
