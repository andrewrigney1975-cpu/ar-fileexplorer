namespace FileExplorer.Services;

/// Maps file system entries to Segoe Fluent Icons glyphs (codepoint escapes to keep this file plain ASCII).
public static class IconHelper
{
    public const string Folder = "\uE8B7";
    public const string FolderOpen = "\uE838";
    public const string Drive = "\uEDA2";
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
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tiff", ".svg" };

    private static readonly HashSet<string> AudioExt = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".flac", ".aac", ".wma", ".ogg", ".m4a" };

    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v" };

    private static readonly HashSet<string> ArchiveExt = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".rar", ".7z", ".tar", ".gz", ".iso" };

    private static readonly HashSet<string> ExeExt = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".msi", ".bat", ".cmd", ".ps1" };

    private static readonly HashSet<string> CodeExt = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".xaml", ".cpp", ".h", ".c", ".py", ".js", ".ts", ".json", ".xml", ".html", ".css", ".java", ".go", ".rs" };

    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".log", ".ini", ".cfg", ".yml", ".yaml" };

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
}
