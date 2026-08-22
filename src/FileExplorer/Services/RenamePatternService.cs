using FileExplorer.Models;

namespace FileExplorer.Services;

/// Pure pattern-substitution logic for batch rename's "Pattern" mode, pulled out of PaneView's
/// code-behind so it can be unit-testable without any UI state.
public static class RenamePatternService
{
    public static string Apply(string pattern, FileSystemItem item, int index)
    {
        var nameNoExt = item.IsDirectory ? item.Name : Path.GetFileNameWithoutExtension(item.Name);
        var result = pattern.Replace("{name}", nameNoExt);

        return System.Text.RegularExpressions.Regex.Replace(result, @"\{n(:([0#]+))?\}", m =>
        {
            var number = index + 1;
            return m.Groups[2].Success ? number.ToString(m.Groups[2].Value) : number.ToString();
        });
    }
}
