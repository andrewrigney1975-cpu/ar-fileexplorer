namespace FileExplorer.Services;

/// File-backed CRUD over the user's saved scripts - each script is a plain .js file, named after
/// the script, under %LocalAppData%\FileExplorerApp\Scripts\. Kept on disk (not a JSON index) so
/// scripts stay transparent/inspectable/copyable outside the app too.
public static class ScriptService
{
    private static readonly string FolderPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorerApp", "Scripts");

    public static List<string> List()
    {
        try
        {
            if (!Directory.Exists(FolderPath))
            {
                return new List<string>();
            }

            return Directory.EnumerateFiles(FolderPath, "*.js")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n is not null)
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    public static string? Load(string name)
    {
        try
        {
            var path = PathFor(name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(string name, string content)
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            File.WriteAllText(PathFor(name), content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static void Delete(string name)
    {
        try
        {
            var path = PathFor(name);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// True if name isn't already used by another script (case-insensitive, matching the filesystem).
    public static bool IsNameAvailable(string name) => !File.Exists(PathFor(name));

    private static string PathFor(string name) => Path.Combine(FolderPath, name + ".js");
}
