using CommunityToolkit.Mvvm.ComponentModel;
using FileExplorer.Services;
using Microsoft.UI.Dispatching;

namespace FileExplorer.ViewModels;

public sealed partial class TabViewModel : ObservableObject
{
    private bool _hasCustomHeader;
    private PaneViewModel _activePane;

    public TabViewModel(DispatcherQueue dispatcher, string startPath, string? name = null)
        : this(dispatcher, startPath, startPath, name)
    {
    }

    public TabViewModel(DispatcherQueue dispatcher, string leftPath, string rightPath, string? name = null)
    {
        LeftPane = new PaneViewModel(dispatcher, leftPath);
        RightPane = new PaneViewModel(dispatcher, rightPath);
        _activePane = LeftPane;

        LeftPane.IsActive = true;
        LeftPane.PathChanged += (_, _) => UpdateHeader();
        RightPane.PathChanged += (_, _) => UpdateHeader();

        if (string.IsNullOrWhiteSpace(name))
        {
            UpdateHeader();
        }
        else
        {
            Rename(name);
        }
    }

    public PaneViewModel LeftPane { get; }
    public PaneViewModel RightPane { get; }

    // Left as a hand-written property (rather than [ObservableProperty]) because the constructor
    // deliberately assigns the backing field directly (`_activePane = LeftPane`) to set the initial
    // active pane without firing this setter's side effects before PathChanged handlers are wired up.
    public PaneViewModel ActivePane
    {
        get => _activePane;
        set
        {
            if (SetProperty(ref _activePane, value))
            {
                LeftPane.IsActive = ReferenceEquals(value, LeftPane);
                RightPane.IsActive = ReferenceEquals(value, RightPane);
                UpdateHeader();
            }
        }
    }

    [ObservableProperty]
    public partial string Header { get; private set; } = "New Workspace";

    /// True once the user has explicitly renamed this workspace, so path changes no longer overwrite the header.
    public bool HasCustomHeader => _hasCustomHeader;

    /// Sets a user-chosen name for this workspace, or (given null/blank) reverts to the auto-generated name.
    public void Rename(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            _hasCustomHeader = false;
            UpdateHeader();
            return;
        }

        _hasCustomHeader = true;
        Header = name;
    }

    public void RefreshBoth()
    {
        LeftPane.Refresh();
        RightPane.Refresh();
    }

    private void UpdateHeader()
    {
        if (_hasCustomHeader)
        {
            return;
        }

        var path = ActivePane.CurrentPath;
        string? name;

        if (RemotePathService.IsRemote(path))
        {
            name = RemotePathService.GetFileName(path);
            if (string.IsNullOrEmpty(name) && RemotePathService.TryParse(path, out _, out var connectionId, out _))
            {
                // At a connection's root - fall back to its saved display name rather than "".
                name = RemoteConnectionService.Find(connectionId)?.Name;
            }
        }
        else
        {
            name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        }

        Header = string.IsNullOrEmpty(path) ? "New Workspace" : (name is { Length: > 0 } n ? n : path);
    }
}
