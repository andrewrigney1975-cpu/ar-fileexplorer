namespace FileExplorer.Models;

/// A configured backup job: a source folder/volume mirrored to a destination on a full + differential
/// cadence. VSS-snapshotted (so open/system files copy cleanly) when SourceRoot is a whole volume.
public sealed class BackupJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;

    /// A drive root ("C:\") for a VSS volume backup, or any folder for a plain file backup.
    public string SourceRoot { get; set; } = string.Empty;

    /// Where backup sets are written - each job gets its own "&lt;DestinationRoot&gt;\&lt;Name&gt;\" tree.
    public string DestinationRoot { get; set; } = string.Empty;

    /// Snapshot the source's volume via VSS before copying (recommended; requires elevation).
    public bool UseVolumeShadowCopy { get; set; } = true;

    public int FullEveryDays { get; set; } = 7;
    public int DifferentialEveryDays { get; set; } = 1;

    /// Keep this many of the most recent full sets (and every differential based on them); older
    /// full sets and their differentials are pruned after each successful run.
    public int KeepFullSets { get; set; } = 3;

    /// Extra directory names (relative to SourceRoot) to skip, on top of the always-excluded
    /// pagefile / hiberfil / "System Volume Information" / the destination itself.
    public string[] ExcludeDirectories { get; set; } = Array.Empty<string>();

    public DateTimeOffset? LastFullUtc { get; set; }
    public DateTimeOffset? LastRunUtc { get; set; }
    public string? LastRunResult { get; set; }
}

public enum BackupSetType
{
    Full,
    Differential,
}

public enum BackupRunMode
{
    /// Full or differential depending on the job's cadence and last full.
    Auto,
    Full,
    Differential,
}

/// Metadata written as set.json inside every set folder, so a set is self-describing and a
/// half-finished run (no CompletedUtc) is skipped by restore + retention.
public sealed record BackupSetManifest(
    BackupSetType Type,
    DateTimeOffset TimestampUtc,
    string SourceRoot,
    string? BaseFullFolder,
    DateTimeOffset? CompletedUtc,
    long FileCount,
    long ByteCount);

public sealed record BackupSet(string FolderPath, string FolderName, BackupSetManifest Manifest)
{
    public bool Completed => Manifest.CompletedUtc is not null;
}

/// One line the elevated backup process appends to its status file; the UI tails it for progress.
public sealed record BackupProgress(
    string Phase,
    string Detail,
    long FilesCopied,
    long BytesCopied,
    bool Finished,
    bool Failed,
    string? Message);
