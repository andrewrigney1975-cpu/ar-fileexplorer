using System.Text.Json;
using FileExplorer.Models;
using Microsoft.Extensions.DependencyInjection;

namespace FileExplorer.Services;

public static class DiskSpaceAnalyserService
{
    public sealed record DriveSummary(DriveInfo Drive, string Label, long UsedBytes, long TotalBytes);

    public static List<DriveSummary> GetDrives()
    {
        var result = new List<DriveSummary>();

        foreach (var drive in App.Services.GetRequiredService<IFileSystemService>().GetReadyDrives())
        {
            var label = string.IsNullOrEmpty(drive.VolumeLabel) ? drive.Name : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";

            try
            {
                result.Add(new DriveSummary(drive, label, drive.TotalSize - drive.TotalFreeSpace, drive.TotalSize));
            }
            catch (IOException ex)
            {
                // 0/0 renders as "empty drive" in the UI, which is misleading if this was actually a
                // read failure rather than a genuinely empty drive - worth a trail, unlike the
                // per-folder ACL noise below (this fires once per drive, not once per protected folder).
                LoggingService.LogWarning($"DiskSpaceAnalyserService.GetDrives: {drive.Name}", ex);
                result.Add(new DriveSummary(drive, label, 0, 0));
            }
        }

        return result;
    }

    /// Sizes and item-counts the immediate children of path (recursively for subfolders),
    /// sorted largest first. Inaccessible entries are skipped rather than failing the whole scan.
    public static async Task<List<SpaceEntry>> AnalyseFolderAsync(string path, CancellationToken token)
    {
        return await Task.Run(() =>
        {
            var entries = new List<SpaceEntry>();

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(path).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return entries;
            }

            foreach (var entry in children)
            {
                token.ThrowIfCancellationRequested();

                if (Directory.Exists(entry))
                {
                    var (size, itemCount) = SumDirectory(entry, token);
                    entries.Add(new SpaceEntry(Path.GetFileName(entry), entry, true, size, itemCount));
                }
                else
                {
                    try
                    {
                        entries.Add(new SpaceEntry(Path.GetFileName(entry), entry, false, new FileInfo(entry).Length, 0));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }

            entries.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
            return entries;
        }, token);
    }

    private static (long Size, int ItemCount) SumDirectory(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        long size = 0;
        int items = 1;

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                token.ThrowIfCancellationRequested();

                if (Directory.Exists(entry))
                {
                    var (subSize, subItems) = SumDirectory(entry, token);
                    size += subSize;
                    items += subItems;
                }
                else
                {
                    try
                    {
                        size += new FileInfo(entry).Length;
                        items++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return (size, items);
    }

    private sealed record SpaceExportEntry(string Name, string FullPath, bool IsDirectory, long SizeBytes, string Size, int ItemCount);

    private sealed record SpaceExport(string FolderPath, string ExportedAt, int ItemCount, IReadOnlyList<SpaceExportEntry> Items);

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    /// Returns the path the export was written to. Filename is the folder's own path with every
    /// path separator/drive-colon/invalid-filename character replaced by an underscore, plus a
    /// yyyyMMddHHmmss export timestamp - same convention as FolderExportService.
    public static string Export(string folderPath, IReadOnlyList<SpaceEntry> entries)
    {
        var export = new SpaceExport(
            folderPath,
            DateTimeOffset.Now.ToString("o"),
            entries.Count,
            entries.Select(e => new SpaceExportEntry(e.Name, e.FullPath, e.IsDirectory, e.SizeBytes, FileSystemItem.FormatSize(e.SizeBytes), e.ItemCount)).ToList());

        var fileName = $"{SanitizeForFileName(folderPath)}_{DateTime.Now:yyyyMMddHHmmss}.json";
        var exportPath = Path.Combine(folderPath, fileName);

        File.WriteAllText(exportPath, JsonSerializer.Serialize(export, SerializerOptions));
        return exportPath;
    }

    private static string SanitizeForFileName(string path)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = path.Select(c => c is '\\' or '/' or ':' || invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim('_');
    }
}
