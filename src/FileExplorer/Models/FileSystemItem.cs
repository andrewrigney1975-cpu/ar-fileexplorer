using FileExplorer.Helpers;
using FileExplorer.Services;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FileExplorer.Models;

public sealed class FileSystemItem : ObservableObject
{
    private BitmapImage? _thumbnail;
    private bool _thumbnailRequested;
    private string? _tagColor;
    private string? _cloudBadge;
    private SyncRole _syncRole;

    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }
    public long SizeBytes { get; init; }
    public DateTimeOffset Modified { get; init; }
    public string Extension { get; init; } = string.Empty;

    /// Color-label name (e.g. "Red"), or null when untagged. Set by FileSystemService on load.
    public string? TagColor
    {
        get => _tagColor;
        set => SetProperty(ref _tagColor, value);
    }

    public string Glyph => IsDirectory ? IconHelper.Folder : IconHelper.GlyphFor(Extension);

    /// Whether this folder is a sync task's source or target (or neither). Set by FileSystemService on load.
    public SyncRole SyncRole
    {
        get => _syncRole;
        set => SetProperty(ref _syncRole, value);
    }

    /// Cloud placeholder glyph (online-only cloud icon or always-available checkmark), or null
    /// outside a detected cloud sync folder. Set by FileSystemService on load.
    public string? CloudBadge
    {
        get => _cloudBadge;
        set => SetProperty(ref _cloudBadge, value);
    }

    /// Decoded lazily the first time this item's Icons-view container is realized.
    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        private set => SetProperty(ref _thumbnail, value);
    }

    public async Task EnsureThumbnailAsync()
    {
        if (_thumbnailRequested || (!IsDirectory && !IconHelper.IsPreviewableImage(Extension)))
        {
            return;
        }

        _thumbnailRequested = true;
        Thumbnail = await ThumbnailCacheService.GetOrCreateAsync(FullPath, Modified, IsDirectory);
    }

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
