using System.Text.Json;
using FileExplorer.Models;

namespace FileExplorer.Services;

/// Exports a folder's Details-view listing (name, date modified, type, size, attributes - exactly
/// the columns PaneView's Details header shows) to a JSON file saved into that same folder.
public static class FolderExportService
{
    private sealed record ExportedEntry(
        string Name,
        string FullPath,
        bool IsDirectory,
        string Kind,
        long SizeBytes,
        string Size,
        string Modified,
        string Attributes);

    private sealed record FolderExport(
        string FolderPath,
        string ExportedAt,
        int ItemCount,
        IReadOnlyList<ExportedEntry> Items);

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    /// Returns the path the export was written to. Filename is the folder's own path with every
    /// path separator/drive-colon/invalid-filename character replaced by an underscore, plus a
    /// yyyyMMddHHmmss export timestamp, so exports from different folders (or repeated exports of
    /// the same folder) never collide.
    public static string Export(string folderPath, IReadOnlyList<FileSystemItem> items)
    {
        var export = new FolderExport(
            folderPath,
            DateTimeOffset.Now.ToString("o"),
            items.Count,
            items.Select(i => new ExportedEntry(
                i.Name,
                i.FullPath,
                i.IsDirectory,
                i.Kind,
                i.SizeBytes,
                i.SizeDisplay,
                i.Modified.ToLocalTime().ToString("o"),
                i.AttributesDisplay)).ToList());

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
