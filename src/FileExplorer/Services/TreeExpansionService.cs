namespace FileExplorer.Services;

/// Remembers which folders are expanded in the left rail's drive tree, so the tree comes back the
/// way it was left on the next launch. Path-keyed like [[TagService]] / [[RatingService]].
public static class TreeExpansionService
{
    private static readonly JsonFileStore<List<string>> Store = new("tree-expansion.json", () => new List<string>());

    private static HashSet<string>? _set;

    private static HashSet<string> Set =>
        _set ??= new HashSet<string>(Store.Load(), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> ExpandedPaths => Set;

    public static void SetExpanded(string path, bool expanded)
    {
        var changed = expanded ? Set.Add(path) : Set.Remove(path);
        if (changed)
        {
            Store.Save(Set.ToList());
        }
    }
}
