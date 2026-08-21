namespace FileExplorer.Services;

public enum FileDropOperation
{
    Move,
    Copy,
    Sync,
}

/// Shared path helpers used by the file operation queue.
public static class FileOperationService
{
    /// True when two paths share the same drive/volume root. A remote path is never "the same
    /// drive" as anything (forcing the queue's normal streamed-copy path instead of the
    /// local-only atomic-rename shortcut, which only makes sense between two real local paths).
    public static bool SameDrive(string pathA, string pathB)
    {
        if (RemotePathService.IsRemote(pathA) || RemotePathService.IsRemote(pathB))
        {
            return false;
        }

        var rootA = Path.GetPathRoot(pathA);
        var rootB = Path.GetPathRoot(pathB);
        return string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase);
    }

    /// False if dropping sourcePaths onto targetFolder would be a no-op or corrupt the tree
    /// (dropping an item onto itself, or a folder into its own subtree). The checks below use
    /// Path.GetFullPath, which assumes a real Windows path - skipped whenever source or target is
    /// remote, since a local<->remote pair can never be "the same location" by construction, and
    /// remote<->remote is rejected separately (FileOperationQueueService.RunJobAsync) before this
    /// would matter anyway.
    public static bool IsValidDropTarget(IEnumerable<string> sourcePaths, string targetFolder)
    {
        if (RemotePathService.IsRemote(targetFolder) || sourcePaths.Any(RemotePathService.IsRemote))
        {
            return true;
        }

        var normalizedTarget = Path.GetFullPath(targetFolder).TrimEnd(Path.DirectorySeparatorChar);

        foreach (var source in sourcePaths)
        {
            var normalizedSource = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);

            if (string.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var parent = Path.GetDirectoryName(normalizedSource);
            if (string.Equals(parent, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                return false; // already there
            }

            if (Directory.Exists(source) &&
                normalizedTarget.StartsWith(normalizedSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false; // would move a folder into its own descendant
            }
        }

        return true;
    }

    /// Appends " (2)", " (3)", ... to avoid overwriting an existing file or folder at the destination.
    public static string MakeUniqueDestination(string destination)
    {
        if (!File.Exists(destination) && !Directory.Exists(destination))
        {
            return destination;
        }

        var dir = Path.GetDirectoryName(destination)!;
        var name = Path.GetFileNameWithoutExtension(destination);
        var ext = Path.GetExtension(destination);

        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
