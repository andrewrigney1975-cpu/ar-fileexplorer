namespace FileExplorer.Services;

/// Maps file system entries to Segoe Fluent Icons glyphs (codepoint escapes to keep this file plain ASCII).
public static class IconHelper
{
    public const string Folder = "\uE8B7";
    public const string FolderOpen = "\uE838";
    public const string Drive = "\uEDA2";
    public const string NetworkDrive = "\uE968";
    public const string GenericFile = "\uE8A5";
    public const string Document = "\uE8A5";
    public const string Image = "\uEB9F";
    public const string Audio = "\uE8D6";
    public const string Video = "\uE714";
    public const string Archive = "\uE7B8";
    public const string Executable = "\uE756";
    public const string Code = "\uE943";
    public const string Pdf = "\uE8A5";

    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tiff", ".svg", ".avif" };

    private static readonly HashSet<string> AudioExt = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".flac", ".aac", ".wma", ".ogg", ".m4a" };

    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v" };

    private static readonly HashSet<string> ArchiveExt = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".iso" };

    /// Formats SharpCompress can read - a subset of ArchiveExt (no .iso; that's a disk image, not
    /// a compression format SharpCompress handles).
    private static readonly HashSet<string> ExtractableArchiveExt = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz" };

    private static readonly HashSet<string> ExeExt = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".msi", ".bat", ".cmd", ".ps1" };

    private static readonly HashSet<string> CodeExt = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".xaml", ".cpp", ".h", ".c", ".py", ".js", ".ts", ".json", ".xml", ".html", ".css", ".java", ".go", ".rs" };

    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".log", ".ini", ".cfg", ".yml", ".yaml" };

    // Modern XML-based Office formats only - the OpenXml SDK can't read legacy .doc/.xls/.ppt.
    private static readonly HashSet<string> OfficeExt = new(StringComparer.OrdinalIgnoreCase)
        { ".docx", ".xlsx", ".pptx" };

    public static string GlyphFor(string extension)
    {
        if (ImageExt.Contains(extension)) return Image;
        if (AudioExt.Contains(extension)) return Audio;
        if (VideoExt.Contains(extension)) return Video;
        if (ArchiveExt.Contains(extension)) return Archive;
        if (ExeExt.Contains(extension)) return Executable;
        if (CodeExt.Contains(extension)) return Code;
        if (TextExt.Contains(extension)) return Document;
        if (string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase)) return Pdf;
        return GenericFile;
    }

    public static bool IsPreviewableImage(string extension) => ImageExt.Contains(extension) && extension != ".svg";

    public static bool IsPreviewableText(string extension) => TextExt.Contains(extension) || CodeExt.Contains(extension);

    public static bool IsCodeExtension(string extension) => CodeExt.Contains(extension);

    public static bool IsPreviewableVideo(string extension) => VideoExt.Contains(extension);

    public static bool IsPreviewablePdf(string extension) => string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase);

    public static bool IsPreviewableOffice(string extension) => OfficeExt.Contains(extension);

    public static bool IsExtractableArchive(string extension) => ExtractableArchiveExt.Contains(extension);
}
