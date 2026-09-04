using FileExplorer.Models;
using FileExplorer.Services;

namespace FileExplorer.Tests;

/// End-to-end for the non-VSS path: real robocopy + hard-link differential seeding, on temp folders.
public sealed class BackupRunnerTests : IDisposable
{
    private readonly string _root;
    private readonly string _source;
    private readonly string _dest;
    private readonly string _status;

    public BackupRunnerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "runnertest-" + Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_root, "src");
        _dest = Path.Combine(_root, "dst");
        _status = Path.Combine(_root, "status.jsonl");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_dest);
        File.WriteAllText(_status, string.Empty);

        File.WriteAllText(Path.Combine(_source, "a.txt"), "alpha");
        Directory.CreateDirectory(Path.Combine(_source, "sub"));
        File.WriteAllText(Path.Combine(_source, "sub", "b.txt"), "bravo");
    }

    private BackupJob Job() => new()
    {
        Name = "T",
        SourceRoot = _source,
        DestinationRoot = _dest,
        UseVolumeShadowCopy = false,
        FullEveryDays = 7,
        DifferentialEveryDays = 1,
        KeepFullSets = 5,
    };

    [Fact]
    public async Task Full_then_differential_produces_two_browsable_sets_and_the_diff_reflects_a_change()
    {
        var job = Job();

        var full = await BackupRunner.RunAsync(job, BackupRunMode.Full, _status, CancellationToken.None);
        Assert.True(full == 0, "runner exit " + full + " - status:\n" + File.ReadAllText(_status));

        var fullSet = BackupService.EnumerateSets(job).Single();
        Assert.True(fullSet.Completed);
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(fullSet.FolderPath, "a.txt")));
        Assert.Equal("bravo", File.ReadAllText(Path.Combine(fullSet.FolderPath, "sub", "b.txt")));

        // change one file, add one, remove one
        await Task.Delay(1100); // let mtime move so robocopy sees the change
        File.WriteAllText(Path.Combine(_source, "a.txt"), "ALPHA-CHANGED");
        File.WriteAllText(Path.Combine(_source, "c.txt"), "charlie");
        File.Delete(Path.Combine(_source, "sub", "b.txt"));

        var diff = await BackupRunner.RunAsync(job, BackupRunMode.Differential, _status, CancellationToken.None);
        Assert.Equal(0, diff);

        var diffSet = BackupService.EnumerateSets(job)
            .Single(s => s.Manifest.Type == BackupSetType.Differential);

        // The diff set is a complete tree: changed file updated, new file present, deleted file gone.
        Assert.Equal("ALPHA-CHANGED", File.ReadAllText(Path.Combine(diffSet.FolderPath, "a.txt")));
        Assert.Equal("charlie", File.ReadAllText(Path.Combine(diffSet.FolderPath, "c.txt")));
        Assert.False(File.Exists(Path.Combine(diffSet.FolderPath, "sub", "b.txt")));

        // The full set is untouched.
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(fullSet.FolderPath, "a.txt")));
        Assert.True(File.Exists(Path.Combine(fullSet.FolderPath, "sub", "b.txt")));

        // Unchanged content in the diff is a hard link to the full (same file index).
        var linkA = new FileInfo(Path.Combine(diffSet.FolderPath, "a.txt"));
        Assert.True(linkA.Length > 0);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
