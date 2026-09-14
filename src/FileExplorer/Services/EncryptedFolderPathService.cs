namespace FileExplorer.Services;

/// Scheme-aware path helpers for browsing inside an unlocked encrypted folder container, mirroring
/// [[RemotePathService]]'s shape: "encryptedfolder://{urlencoded absolute .dxlock path}/{relative/path}"
/// - unlike Virtual Folders (flat, no real subtree), an encrypted folder preserves its original
/// nested structure, so this needs the same relative-path/breadcrumb machinery a remote connection does.
public static class EncryptedFolderPathService
{
    public const string Scheme = "encryptedfolder";
    private const string Prefix = Scheme + "://";

    public static bool IsEncrypted(string path) => path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static string BuildRoot(string containerPath) => $"{Prefix}{Uri.EscapeDataString(containerPath)}/";

    /// relativePath is always "/"-rooted ("/" for the container's own root). Segments are plain
    /// file/folder names, never escaped - '/' can't appear in a Windows file name, so there's no
    /// ambiguity joining them directly.
    public static bool TryParse(string path, out string containerPath, out string relativePath)
    {
        containerPath = string.Empty;
        relativePath = string.Empty;

        if (!IsEncrypted(path))
        {
            return false;
        }

        var rest = path[Prefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash < 0)
        {
            containerPath = Uri.UnescapeDataString(rest);
            relativePath = "/";
            return true;
        }

        containerPath = Uri.UnescapeDataString(rest[..slash]);
        relativePath = rest[slash..];
        if (string.IsNullOrEmpty(relativePath))
        {
            relativePath = "/";
        }

        return true;
    }

    public static string Combine(string basePath, string childName)
    {
        if (!TryParse(basePath, out var containerPath, out var relativePath))
        {
            return Path.Combine(basePath, childName);
        }

        var trimmed = relativePath.TrimEnd('/');
        return $"{Prefix}{Uri.EscapeDataString(containerPath)}{trimmed}/{childName}";
    }

    /// Null return means "at the encrypted folder's own root" - same convention RemotePathService
    /// uses for a connection root: the real parent folder containing the .dxlock file is
    /// deliberately not exposed as a navigable "up" target.
    public static string? GetParent(string path)
    {
        if (!TryParse(path, out var containerPath, out var relativePath))
        {
            return null;
        }

        var trimmed = relativePath.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return null;
        }

        var lastSlash = trimmed.LastIndexOf('/');
        var parentPath = lastSlash <= 0 ? "/" : trimmed[..lastSlash];
        return $"{Prefix}{Uri.EscapeDataString(containerPath)}{parentPath}";
    }

    public static string GetFileName(string path)
    {
        if (!TryParse(path, out var containerPath, out var relativePath))
        {
            return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        }

        var trimmed = relativePath.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            // At the container's own root - display name is the container's filename minus the
            // .dxlock extension, so it reads the same as the folder did before encryption.
            return Path.GetFileNameWithoutExtension(containerPath);
        }

        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash < 0 ? trimmed[1..] : trimmed[(lastSlash + 1)..];
    }

    /// Breadcrumb segments for the relative-path portion only, each paired with the full path
    /// navigating to it - the container's own display name is prepended separately by the caller
    /// (PaneView), matching how RemotePathService.GetBreadcrumbSegments is used.
    public static IReadOnlyList<(string Name, string Path)> GetBreadcrumbSegments(string path)
    {
        if (!TryParse(path, out var containerPath, out var relativePath))
        {
            return Array.Empty<(string, string)>();
        }

        var segments = new List<(string Name, string Path)>();
        var parts = relativePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var accumulated = string.Empty;

        foreach (var part in parts)
        {
            accumulated += "/" + part;
            segments.Add((part, $"{Prefix}{Uri.EscapeDataString(containerPath)}{accumulated}"));
        }

        return segments;
    }

    /// Normalizes a "/"-rooted relative path to the key shape used inside the container's index
    /// (no leading/trailing slash, "" for the root itself).
    public static string NormalizeIndexKey(string relativePath) => relativePath.Trim('/');
}
