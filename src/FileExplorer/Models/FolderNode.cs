using FileExplorer.Helpers;
using FileExplorer.Services;

namespace FileExplorer.Models;

/// Content payload for a node in the left-rail drive/folder TreeView.
public sealed class FolderNode : ObservableObject
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDrive { get; init; }
    public bool IsNetwork { get; init; }
    public string Glyph => IsNetwork ? IconHelper.NetworkDrive : IsDrive ? IconHelper.Drive : IconHelper.Folder;

    /// 0-100 used-space percentage; null for non-drive nodes (hides the usage bar).
    public double? UsedPercent { get; init; }
    public string? UsageText { get; init; }
}
