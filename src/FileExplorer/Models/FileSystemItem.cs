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
    private bool _isWatched;

    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }
    public long SizeBytes { get; init; }
    public DateTimeOffset Modified { get; init; }
    public string Extension { get; init; } = string.Empty;
    public FileAttributes Attributes { get; init; }

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
    public string AttributesDisplay
    {
        get
        {
            var letters = string.Empty;
            if (Attributes.HasFlag(FileAttributes.ReadOnly)) letters += "R";
            if (Attributes.HasFlag(FileAttributes.Hidden)) letters += "H";
            if (Attributes.HasFlag(FileAttributes.System)) letters += "S";
            if (Attributes.HasFlag(FileAttributes.Archive)) letters += "A";
            if (Attributes.HasFlag(FileAttributes.Compressed)) letters += "C";
            if (Attributes.HasFlag(FileAttributes.Encrypted)) letters += "E";
            if (Attributes.HasFlag(FileAttributes.ReparsePoint)) letters += "L";
            if (Attributes.HasFlag(FileAttributes.Temporary)) letters += "T";
            if (Attributes.HasFlag(FileAttributes.Offline)) letters += "O";
            if (Attributes.HasFlag(FileAttributes.NotContentIndexed)) letters += "I";
            if (Attributes.HasFlag(FileAttributes.SparseFile)) letters += "P";
            return letters;
        }
    }

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

    /// Whether files added to this folder trigger a script run. Set by FileSystemService on load.
    public bool IsWatched
    {
        get => _isWatched;
        set => SetProperty(ref _isWatched, value);
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
        private set
        {
            if (SetProperty(ref _thumbnail, value))
            {
                OnPropertyChanged(nameof(IsFolderThumbnail));
            }
        }
    }

    /// True once a folder has been given a derived-image thumbnail (found inside it) rather than
    /// still showing the plain folder glyph - drives the small folder-icon overlay that marks it as
    /// a folder, not a file, once its own thumbnail is covering the usual glyph.
    public bool IsFolderThumbnail => IsDirectory && Thumbnail is not null;

    public async Task EnsureThumbnailAsync()
    {
        if (_thumbnailRequested || (!IsDirectory && !IconHelper.IsPreviewableImage(Extension)))
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
