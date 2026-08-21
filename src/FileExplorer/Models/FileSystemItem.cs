using CommunityToolkit.Mvvm.ComponentModel;
using FileExplorer.Services;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FileExplorer.Models;

public sealed partial class FileSystemItem : ObservableObject
{
    private bool _thumbnailRequested;

    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }
    public long SizeBytes { get; init; }
    public DateTimeOffset Modified { get; init; }
    public string Extension { get; init; } = string.Empty;
    public FileAttributes Attributes { get; init; }

    /// True for an item listed from an FTP/SFTP connection rather than the local filesystem.
    /// Local-only concepts (thumbnails, symlinks, folder watch/sync, tags, Undo) are hidden or
    /// skipped for these - see RemotePathService for the "scheme://connectionId/path" shape.
    public bool IsRemote => RemotePathService.IsRemote(FullPath);

    /// None unless Attributes has ReparsePoint set. Set by FileSystemService on load.
    public ReparsePointKind LinkKind { get; init; } = ReparsePointKind.None;

    /// The link's own (unresolved-further) target path, or null when LinkKind is None. Set by
    /// FileSystemService on load.
    public string? LinkTarget { get; init; }

    public bool IsLink => LinkKind != ReparsePointKind.None;

    /// Small badge glyph shown over the icon for a link, or null otherwise (Segoe Fluent Icons "Link").
    public string? LinkGlyph => IsLink ? "" : null;

    /// Windows Explorer-style attribute letter codes (see
    /// https://learn.microsoft.com/en-us/windows/win32/fileio/file-attribute-constants),
    /// most-common-first: Read-only, Hidden, System, Archive, then the less common ones.
    public string AttributesDisplay => FormatAttributes(Attributes);

    public static string FormatAttributes(FileAttributes attributes)
    {
        var letters = string.Empty;
        if (attributes.HasFlag(FileAttributes.ReadOnly)) letters += "R";
        if (attributes.HasFlag(FileAttributes.Hidden)) letters += "H";
        if (attributes.HasFlag(FileAttributes.System)) letters += "S";
        if (attributes.HasFlag(FileAttributes.Archive)) letters += "A";
        if (attributes.HasFlag(FileAttributes.Compressed)) letters += "C";
        if (attributes.HasFlag(FileAttributes.Encrypted)) letters += "E";
        if (attributes.HasFlag(FileAttributes.ReparsePoint)) letters += "L";
        if (attributes.HasFlag(FileAttributes.Temporary)) letters += "T";
        if (attributes.HasFlag(FileAttributes.Offline)) letters += "O";
        if (attributes.HasFlag(FileAttributes.NotContentIndexed)) letters += "I";
        if (attributes.HasFlag(FileAttributes.SparseFile)) letters += "P";
        return letters;
    }

    /// Color-label name (e.g. "Red"), or null when untagged. Set by FileSystemService on load.
    [ObservableProperty]
    public partial string? TagColor { get; set; }

    public string Glyph => IsDirectory ? IconHelper.Folder : IconHelper.GlyphFor(Extension);

    /// Whether this folder is a sync task's source or target (or neither). Set by FileSystemService on load.
    [ObservableProperty]
    public partial SyncRole SyncRole { get; set; }

    /// Whether files added to this folder trigger a script run. Set by FileSystemService on load.
    [ObservableProperty]
    public partial bool IsWatched { get; set; }

    /// Cloud placeholder glyph (online-only cloud icon or always-available checkmark), or null
    /// outside a detected cloud sync folder. Set by FileSystemService on load.
    [ObservableProperty]
    public partial string? CloudBadge { get; set; }

    /// Decoded lazily the first time this item's Icons-view container is realized.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFolderThumbnail))]
    public partial BitmapImage? Thumbnail { get; private set; }

    /// True once a folder has been given a derived-image thumbnail (found inside it) rather than
    /// still showing the plain folder glyph - drives the small folder-icon overlay that marks it as
    /// a folder, not a file, once its own thumbnail is covering the usual glyph.
    public bool IsFolderThumbnail => IsDirectory && Thumbnail is not null;

    public async Task EnsureThumbnailAsync()
    {
        if (_thumbnailRequested || IsRemote || (!IsDirectory && !IconHelper.IsPreviewableImage(Extension)))
        {
            return;
        }

        _thumbnailRequested = true;
        Thumbnail = await ThumbnailCacheService.GetOrCreateAsync(FullPath, Modified, IsDirectory);
    }

    public string Kind => LinkKind switch
    {
        ReparsePointKind.Junction => "Junction",
        ReparsePointKind.SymbolicLink when IsDirectory => "Symbolic Link (folder)",
        ReparsePointKind.SymbolicLink => "Symbolic Link",
        _ => IsDirectory
            ? "File folder"
            : (string.IsNullOrEmpty(Extension) ? "File" : $"{Extension.TrimStart('.').ToUpperInvariant()} File"),
    };

    public string SizeDisplay => IsDirectory ? string.Empty : FormatSize(SizeBytes);

    public string ModifiedDisplay => FormatDate(Modified.ToLocalTime());

    /// Short date + time in the user's Windows regional format (e.g. dd/MM/yyyy HH:mm for
    /// Australian settings) - every date shown anywhere in the app goes through this, not a
    /// hardcoded pattern, so it always matches CurrentCulture rather than assuming US-style dates.
    public static string FormatDate(DateTimeOffset value) => value.ToString("g");

    public static string FormatDate(DateTime value) => value.ToString("g");

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
