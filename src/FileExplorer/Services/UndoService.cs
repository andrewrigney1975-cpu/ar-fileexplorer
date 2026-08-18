namespace FileExplorer.Services;

/// A small undo stack for New Folder, Rename, Move, and Copy actions performed through the app.
public sealed class UndoService
{
    public static UndoService Instance { get; } = new();

    private readonly Stack<UndoAction> _stack = new();

    private UndoService() { }

    public event EventHandler? Changed;

    public bool CanUndo => _stack.Count > 0;

    public string? PeekDescription => _stack.Count > 0 ? _stack.Peek().Description : null;

    public void Push(UndoAction action)
    {
        _stack.Push(action);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task UndoAsync()
    {
        if (_stack.Count == 0)
        {
            return;
        }

        var action = _stack.Pop();
        Changed?.Invoke(this, EventArgs.Empty);
        await action.UndoAsync();
    }
}
