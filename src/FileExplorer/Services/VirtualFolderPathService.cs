namespace FileExplorer.Services;

/// Scheme-aware helpers for virtual folder locations, mirroring [[RemotePathService]]. A virtual
/// folder's path is the flat shape "virtualfolder://{id}" (id is a VirtualFolder.Id GUID) - there is
/// no sub-path, since a virtual folder aggregates real paths rather than containing a real subtree.
public static class VirtualFolderPathService
{
    public const string Scheme = "virtualfolder";
    private const string Prefix = Scheme + "://";

    public static bool IsVirtual(string path) => path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static string BuildPath(string id) => Prefix + id;

    public static bool TryParse(string path, out string id)
    {
        id = string.Empty;
        if (!IsVirtual(path))
        {
            return false;
        }

        id = path[Prefix.Length..];
        return true;
    }
}
