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
