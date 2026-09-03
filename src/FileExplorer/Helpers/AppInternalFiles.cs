namespace FileExplorer.Helpers;

/// Files the app itself writes into arbitrary user folders (the per-folder thumbnail cache, the
/// transient disk-benchmark test file). They're hidden, but scripts and folder-watch triggers can
/// still see them via the raw filesystem - this is the single check that keeps a rename/move
/// script (or a watch that fires on "any file added") from touching them.
public static class AppInternalFiles
{
    private static readonly string[] ExactNames =
    {
        ".docket-thumbs.cache",
        ".arexx-thumbs.cache", // legacy, from before the enfyl Explorer -> Docket rename
    };

    private static readonly (string Prefix, string Suffix)[] Patterns =
    {
        (".docket-benchmark-", ".tmp"),
        (".arexx-benchmark-", ".tmp"),
    };

    public static bool IsInternal(string path)
    {
        var name = Path.GetFileName(path.AsSpan().TrimEnd(Path.DirectorySeparatorChar)).ToString();

        foreach (var exact in ExactNames)
        {
            if (name.Equals(exact, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var (prefix, suffix) in Patterns)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
