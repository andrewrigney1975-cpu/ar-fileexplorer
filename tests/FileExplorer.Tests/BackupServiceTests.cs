using FileExplorer.Models;
using FileExplorer.Services;

namespace FileExplorer.Tests;

/// Covers the disk-bookkeeping side of BackupService (set enumeration, full/diff decision,
/// retention). The job-store CRUD is left alone here - it writes a real shared file.
public sealed class BackupServiceTests : IDisposable
{
    private readonly string _dest;
    private readonly BackupJob _job;

    public BackupServiceTests()
    {
        _dest = Path.Combine(Path.GetTempPath(), "bkptest-" + Guid.NewGuid().ToString("N"));
        _job = new BackupJob { Name = "Docs", SourceRoot = @"C:\Docs", DestinationRoot = _dest, FullEveryDays = 7, KeepFullSets = 2 };
    }

    private void WriteSet(BackupSetType type, DateTimeOffset ts, string? baseFull = null, bool completed = true)
    {
        var folder = Path.Combine(BackupService.JobDirectory(_job), BackupService.NewSetFolderName(type, ts));
        Directory.CreateDirectory(folder);
        BackupService.WriteManifest(folder, new BackupSetManifest(
            type, ts, _job.SourceRoot, baseFull, completed ? ts.AddMinutes(5) : null, 10, 1024));
    }

    private static DateTimeOffset DaysAgo(double d) => DateTimeOffset.UtcNow.AddDays(-d);

    [Fact]
    public void EnumerateSets_returns_completed_sets_oldest_first_and_skips_partial_ones()
    {
        WriteSet(BackupSetType.Full, DaysAgo(10));
        WriteSet(BackupSetType.Full, DaysAgo(2));
        WriteSet(BackupSetType.Differential, DaysAgo(1), completed: false);

        var sets = BackupService.EnumerateSets(_job);

        Assert.Equal(3, sets.Count);
        Assert.True(sets[0].Manifest.TimestampUtc < sets[1].Manifest.TimestampUtc);
        Assert.False(sets[2].Completed);
        Assert.Equal(DaysAgo(2).Date, BackupService.LatestCompletedFull(_job)!.Manifest.TimestampUtc.Date);
    }

    [Fact]
    public void ResolveMode_is_Full_when_no_full_exists()
    {
        Assert.Equal(BackupRunMode.Full, BackupService.ResolveMode(_job, BackupRunMode.Auto, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ResolveMode_is_Differential_within_the_full_window_and_Full_after_it()
    {
        WriteSet(BackupSetType.Full, DaysAgo(3));
        Assert.Equal(BackupRunMode.Differential, BackupService.ResolveMode(_job, BackupRunMode.Auto, DateTimeOffset.UtcNow));

        WriteSet(BackupSetType.Full, DaysAgo(8));
        // still Differential - the newest full is 3 days old
        Assert.Equal(BackupRunMode.Differential, BackupService.ResolveMode(_job, BackupRunMode.Auto, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ResolveMode_honours_an_explicit_request()
    {
        WriteSet(BackupSetType.Full, DaysAgo(1));
        Assert.Equal(BackupRunMode.Full, BackupService.ResolveMode(_job, BackupRunMode.Full, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SetsToPrune_keeps_the_newest_N_fulls_and_their_diffs()
    {
        var t30 = DaysAgo(30);
        var t28 = DaysAgo(28);
        var t14 = DaysAgo(14);
        var t2 = DaysAgo(2);
        var t1 = DaysAgo(1);

        var oldFull = BackupService.NewSetFolderName(BackupSetType.Full, t30);
        var midFull = BackupService.NewSetFolderName(BackupSetType.Full, t14);
        var newFull = BackupService.NewSetFolderName(BackupSetType.Full, t2);

        WriteSet(BackupSetType.Full, t30);
        WriteSet(BackupSetType.Differential, t28, baseFull: oldFull);
        WriteSet(BackupSetType.Full, t14);
        WriteSet(BackupSetType.Full, t2);
        WriteSet(BackupSetType.Differential, t1, baseFull: newFull);

        var prune = BackupService.SetsToPrune(_job).Select(Path.GetFileName).ToList(); // KeepFullSets = 2

        // Keeps midFull + newFull (and newFull's diff); prunes oldFull and its orphaned diff.
        Assert.Equal(2, prune.Count);
        Assert.Contains(oldFull, prune);
        Assert.Contains(BackupService.NewSetFolderName(BackupSetType.Differential, t28), prune);
        Assert.DoesNotContain(newFull, prune);
        Assert.DoesNotContain(midFull, prune);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dest, recursive: true); } catch (IOException) { }
    }
}
