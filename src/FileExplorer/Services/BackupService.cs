using System.Text.Json;
using FileExplorer.Models;

namespace FileExplorer.Services;

/// Job storage + set bookkeeping for the backup feature. Pure BCL (no WinUI, no elevation) so it's
/// unit-testable; the actual copy work lives in BackupRunner.
public static class BackupService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly JsonFileStore<List<BackupJob>> Store = new("backup-jobs.json", () => new());

    public static event EventHandler? Changed;

    public static IReadOnlyList<BackupJob> All => Store.Load();

    public static BackupJob? Find(string id) => Store.Load().FirstOrDefault(j => j.Id == id);

    public static void AddOrUpdate(BackupJob job)
    {
        var list = Store.Load();
        list.RemoveAll(j => j.Id == job.Id);
        list.Add(job);
        Store.Save(list);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Remove(string id)
    {
        var list = Store.Load();
        if (list.RemoveAll(j => j.Id == id) > 0)
        {
            Store.Save(list);
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    // ----- set bookkeeping -----

    public static string JobDirectory(BackupJob job) =>
        Path.Combine(job.DestinationRoot, Sanitize(job.Name));

    public static string NewSetFolderName(BackupSetType type, DateTimeOffset timestamp) =>
        $"{(type == BackupSetType.Full ? "Full" : "Diff")} {timestamp.LocalDateTime:yyyy-MM-dd HH-mm-ss}";

    /// Every completed set for a job, oldest first.
    public static List<BackupSet> EnumerateSets(BackupJob job)
    {
        var result = new List<BackupSet>();
        var jobDir = JobDirectory(job);
        if (!Directory.Exists(jobDir))
        {
            return result;
        }

        foreach (var dir in Directory.EnumerateDirectories(jobDir))
        {
            var manifestPath = Path.Combine(dir, "set.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                var manifest = JsonSerializer.Deserialize<BackupSetManifest>(File.ReadAllText(manifestPath));
                if (manifest is not null)
                {
                    result.Add(new BackupSet(dir, Path.GetFileName(dir), manifest));
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                LoggingService.LogWarning($"BackupService.EnumerateSets: {manifestPath}", ex);
            }
        }

        return result.OrderBy(s => s.Manifest.TimestampUtc).ToList();
    }

    public static BackupSet? LatestCompletedFull(BackupJob job) =>
        EnumerateSets(job).Where(s => s.Completed && s.Manifest.Type == BackupSetType.Full)
            .OrderByDescending(s => s.Manifest.TimestampUtc).FirstOrDefault();

    /// Decides whether a run should be full or differential: full if there's no completed full, or
    /// the newest one is older than FullEveryDays.
    public static BackupRunMode ResolveMode(BackupJob job, BackupRunMode requested, DateTimeOffset nowUtc)
    {
        if (requested != BackupRunMode.Auto)
        {
            return requested;
        }

        var latestFull = LatestCompletedFull(job);
        if (latestFull is null || nowUtc - latestFull.Manifest.TimestampUtc >= TimeSpan.FromDays(job.FullEveryDays))
        {
            return BackupRunMode.Full;
        }

        return BackupRunMode.Differential;
    }

    /// Set folders to delete under a retention policy: keep the newest KeepFullSets full sets and
    /// every differential based on one of them; everything older (and orphaned differentials) goes.
    public static List<string> SetsToPrune(BackupJob job)
    {
        var sets = EnumerateSets(job).Where(s => s.Completed).ToList();

        var keptFulls = sets.Where(s => s.Manifest.Type == BackupSetType.Full)
            .OrderByDescending(s => s.Manifest.TimestampUtc)
            .Take(Math.Max(1, job.KeepFullSets))
            .Select(s => s.FolderName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return sets
            .Where(s => s.Manifest.Type == BackupSetType.Full
                ? !keptFulls.Contains(s.FolderName)
                : s.Manifest.BaseFullFolder is null || !keptFulls.Contains(s.Manifest.BaseFullFolder))
            .Select(s => s.FolderPath)
            .ToList();
    }

    public static void WriteManifest(string setFolder, BackupSetManifest manifest) =>
        File.WriteAllText(Path.Combine(setFolder, "set.json"), JsonSerializer.Serialize(manifest, Json));

    public static BackupSetManifest? ReadManifest(string setFolder)
    {
        var path = Path.Combine(setFolder, "set.json");
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<BackupSetManifest>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            LoggingService.LogWarning($"BackupService.ReadManifest: {path}", ex);
            return null;
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "Backup" : cleaned;
    }
}
