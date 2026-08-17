using FileExplorer.Helpers;
using FileExplorer.Services;

namespace FileExplorer.Models;

public sealed class FileSystemItem : ObservableObject
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }
    public long SizeBytes { get; init; }
    public DateTimeOffset Modified { get; init; }
    public string Extension { get; init; } = string.Empty;

    public string Glyph => IsDirectory ? IconHelper.Folder : IconHelper.GlyphFor(Extension);

    public string Kind => IsDirectory
        ? "File folder"
        : (string.IsNullOrEmpty(Extension) ? "File" : $"{Extension.TrimStart('.').ToUpperInvariant()} File");

    public string SizeDisplay => IsDirectory ? string.Empty : FormatSize(SizeBytes);

    public string ModifiedDisplay => Modified.ToLocalTime().ToString("MM/dd/yyyy h:mm tt");

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{size:0} {units[unit]}" : $"{size:0.#} {units[unit]}";
    }
}
