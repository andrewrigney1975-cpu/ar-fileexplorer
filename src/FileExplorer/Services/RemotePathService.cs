using FileExplorer.Models;

namespace FileExplorer.Services;

/// Scheme-aware equivalents of the Path.*/Directory.GetParent calls the rest of the app uses for
/// navigation - a remote location is a plain string shaped like "sftp://{connectionId}/{remote/path}"
/// or "ftp://{connectionId}/{remote/path}" (connectionId is a RemoteConnection.Id GUID; host and
/// credentials are never embedded in the path itself, only looked up through that id).
public static class RemotePathService
{
    public const string FtpScheme = "ftp";
    public const string FtpsScheme = "ftps";
    public const string SftpScheme = "sftp";

    public static string SchemeFor(RemoteProtocol protocol) => protocol switch
    {
        RemoteProtocol.Sftp => SftpScheme,
        RemoteProtocol.Ftps => FtpsScheme,
        _ => FtpScheme,
    };

    public static bool IsRemote(string path) =>
        path.StartsWith(FtpScheme + "://", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(FtpsScheme + "://", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(SftpScheme + "://", StringComparison.OrdinalIgnoreCase);

    public static string BuildRoot(string scheme, string connectionId) => $"{scheme}://{connectionId}/";

    public static bool TryParse(string path, out string scheme, out string connectionId, out string remotePath)
    {
        scheme = string.Empty;
        connectionId = string.Empty;
        remotePath = string.Empty;

        if (!IsRemote(path))
        {
            return false;
        }

        var schemeEnd = path.IndexOf("://", StringComparison.Ordinal);
        scheme = path[..schemeEnd];
        var rest = path[(schemeEnd + 3)..];

        var slash = rest.IndexOf('/');
        if (slash < 0)
        {
            connectionId = rest;
            remotePath = "/";
            return true;
        }

        connectionId = rest[..slash];
        remotePath = rest[slash..];
        if (string.IsNullOrEmpty(remotePath))
        {
            remotePath = "/";
        }

        return true;
    }

    public static string Combine(string basePath, string childName)
    {
        if (!TryParse(basePath, out var scheme, out var connectionId, out var remotePath))
        {
            return Path.Combine(basePath, childName);
        }

        var trimmed = remotePath.TrimEnd('/');
        return $"{scheme}://{connectionId}{trimmed}/{childName}";
    }

    /// Null return means "already at the root" - same convention as Directory.GetParent
    /// returning null for a drive root, which CanNavigateUp already relies on.
    public static string? GetParent(string path)
    {
        if (!TryParse(path, out var scheme, out var connectionId, out var remotePath))
        {
            return Directory.GetParent(path)?.FullName;
        }

        var trimmed = remotePath.TrimEnd('/');
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var lastSlash = trimmed.LastIndexOf('/');
        var parentPath = lastSlash <= 0 ? "/" : trimmed[..lastSlash];
        return $"{scheme}://{connectionId}{parentPath}";
    }

    public static string GetFileName(string path)
    {
        if (!TryParse(path, out _, out _, out var remotePath))
        {
            return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        }

        var trimmed = remotePath.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash < 0 ? trimmed : trimmed[(lastSlash + 1)..];
    }

    /// Breadcrumb segments for the remote-path portion only, each paired with the full path
    /// navigating to it - the connection's own display Name is prepended separately by the
    /// caller (PaneView), which has access to the saved RemoteConnection to look it up.
    public static IReadOnlyList<(string Name, string Path)> GetBreadcrumbSegments(string path)
    {
        if (!TryParse(path, out var scheme, out var connectionId, out var remotePath))
        {
            return Array.Empty<(string, string)>();
        }

        var segments = new List<(string Name, string Path)>();
        var parts = remotePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var accumulated = string.Empty;

        foreach (var part in parts)
        {
            accumulated += "/" + part;
            segments.Add((part, $"{scheme}://{connectionId}{accumulated}"));
        }

        return segments;
    }
}
