using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileExplorer.Services;
using Microsoft.UI.Dispatching;

namespace FileExplorer.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcher;
    private readonly IFileSystemService _fileSystemService;
    private readonly ISessionService _sessionService;
    private readonly IRemoteConnectionService _remoteConnectionService;

    public MainViewModel(DispatcherQueue dispatcher, IFileSystemService fileSystemService, ISessionService sessionService, IRemoteConnectionService remoteConnectionService)
    {
        _dispatcher = dispatcher;
        _fileSystemService = fileSystemService;
        _sessionService = sessionService;
        _remoteConnectionService = remoteConnectionService;
        RestoreSessionOrDefault();
    }

    public ObservableCollection<TabViewModel> Tabs { get; } = new();

    [ObservableProperty]
    public partial TabViewModel? SelectedTab { get; set; }

    [RelayCommand]
    private void NewTab() => AddTab();

    public TabViewModel AddTab(string? startPath = null)
    {
        var path = startPath ?? GetDefaultStartPath();
        var tab = new TabViewModel(_dispatcher, _fileSystemService, _remoteConnectionService, path);
        Tabs.Add(tab);
        SelectedTab = tab;
        return tab;
    }

    public TabViewModel AddTab(string leftPath, string rightPath)
    {
        var tab = new TabViewModel(_dispatcher, _fileSystemService, _remoteConnectionService, leftPath, rightPath);
        Tabs.Add(tab);
        SelectedTab = tab;
        return tab;
    }

    /// Like AddTab(startPath), but with an explicit custom header instead of the usual
    /// derived-from-path one - used by "Search Everywhere" so a result opens in its own "Search
    /// Results" workspace rather than overwriting whatever the active pane was showing.
    public TabViewModel AddNamedTab(string startPath, string name)
    {
        var tab = new TabViewModel(_dispatcher, _fileSystemService, _remoteConnectionService, startPath, name);
        Tabs.Add(tab);
        SelectedTab = tab;
        return tab;
    }

    /// AddNamedTab with distinct left/right start paths - used by "Search Everywhere" so following a
    /// folder result lands the folder in the left pane and its parent in the right.
    public TabViewModel AddNamedTab(string leftPath, string rightPath, string name)
    {
        var tab = new TabViewModel(_dispatcher, _fileSystemService, _remoteConnectionService, leftPath, rightPath, name);
        Tabs.Add(tab);
        SelectedTab = tab;
        return tab;
    }

    [RelayCommand]
    public void DuplicateTab(TabViewModel source)
    {
        var name = source.HasCustomHeader ? source.Header : null;
        var tab = new TabViewModel(_dispatcher, _fileSystemService, _remoteConnectionService, source.LeftPane.CurrentPath, source.RightPane.CurrentPath, name);
        var index = Tabs.IndexOf(source);
        Tabs.Insert(index < 0 ? Tabs.Count : index + 1, tab);
        SelectedTab = tab;
    }

    private void RestoreSessionOrDefault()
    {
        var saved = _sessionService.Load();
        if (saved.Count == 0)
        {
            AddTab();
            return;
        }

        foreach (var state in saved)
        {
            Tabs.Add(new TabViewModel(_dispatcher, _fileSystemService, _remoteConnectionService, state.LeftPath, state.RightPath, state.Name));
        }

        SelectedTab = Tabs[0];
    }

    public void SaveSession()
    {
        _sessionService.Save(Tabs.Select(t => new TabState(
            t.LeftPane.CurrentPath, t.RightPane.CurrentPath, t.HasCustomHeader ? t.Header : null)));
    }

    [RelayCommand]
    public void CloseTab(TabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        Tabs.RemoveAt(index);

        if (Tabs.Count == 0)
        {
            AddTab();
            return;
        }

        if (ReferenceEquals(SelectedTab, tab))
        {
            SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        }
    }

    public void RefreshAllPanes()
    {
        foreach (var tab in Tabs)
        {
            tab.RefreshBoth();
        }
    }

    public static string GetDefaultStartPath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Directory.Exists(userProfile) ? userProfile : Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
    }
}
