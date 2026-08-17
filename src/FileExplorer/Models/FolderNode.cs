using FileExplorer.Helpers;
using FileExplorer.Services;

namespace FileExplorer.Models;

/// Content payload for a node in the left-rail drive/folder TreeView.
public sealed class FolderNode : ObservableObject
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDrive { get; init; }
    public string Glyph => IsDrive ? IconHelper.Drive : IconHelper.Folder;
}
