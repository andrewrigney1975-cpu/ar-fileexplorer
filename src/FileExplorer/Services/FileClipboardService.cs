namespace FileExplorer.Services;

/// App-wide cut/copy clipboard, shared between the toolbar and the item context menu.
public sealed class FileClipboardService
{
    public static FileClipboardService Instance { get; } = new();

    private FileClipboardService() { }

    public IReadOnlyList<string>? Paths { get; private set; }

    public bool IsCut { get; private set; }

    public bool HasContent => Paths is { Count: > 0 };

    public void Set(IReadOnlyList<string> paths, bool isCut)
    {
        Paths = paths;
        IsCut = isCut;
    }

    public void Clear() => Paths = null;

    /// Enqueues the clipboard contents (move for cut, copy for copy) into the given folder.
    /// Cut is consumed after one successful paste, matching Explorer.
    public void PasteInto(string destination)
    {
        if (!HasContent || Paths is not { } paths)
        {
            return;
        }

        if (!FileOperationService.IsValidDropTarget(paths, destination))
        {
            return;
        }

        var op = IsCut ? FileDropOperation.Move : FileDropOperation.Copy;
        FileOperationQueueService.Current?.Enqueue(paths, destination, op);

        if (IsCut)
        {
            Clear();
        }
    }
}
