using FileExplorer.Helpers;
using Microsoft.UI.Dispatching;

namespace FileExplorer.ViewModels;

public sealed class TabViewModel : ObservableObject
{
    private PaneViewModel _activePane;
    private string _header = "New Tab";

    public TabViewModel(DispatcherQueue dispatcher, string startPath)
    {
        LeftPane = new PaneViewModel(dispatcher, startPath);
        RightPane = new PaneViewModel(dispatcher, startPath);
        _activePane = LeftPane;

        LeftPane.IsActive = true;
        LeftPane.PathChanged += (_, _) => UpdateHeader();
        RightPane.PathChanged += (_, _) => UpdateHeader();

        UpdateHeader();
    }

    public PaneViewModel LeftPane { get; }
    public PaneViewModel RightPane { get; }

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

    public string Header
    {
        get => _header;
        private set => SetProperty(ref _header, value);
    }

    public void RefreshBoth()
    {
        LeftPane.Refresh();
        RightPane.Refresh();
    }

    private void UpdateHeader()
    {
        var path = ActivePane.CurrentPath;
        Header = string.IsNullOrEmpty(path) ? "New Tab" : (Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : path);
    }
}
