using System.Collections.ObjectModel;
using FileExplorer.Helpers;
using FileExplorer.Services;
using Microsoft.UI.Dispatching;

namespace FileExplorer.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcher;
    private TabViewModel? _selectedTab;

    public MainViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        NewTabCommand = new RelayCommand(() => AddTab());
        CloseTabCommand = new RelayCommand(o => { if (o is TabViewModel tab) CloseTab(tab); });
        DuplicateTabCommand = new RelayCommand(o => { if (o is TabViewModel tab) DuplicateTab(tab); });

        RestoreSessionOrDefault();
    }

    public ObservableCollection<TabViewModel> Tabs { get; } = new();

    public TabViewModel? SelectedTab
    {
        get => _selectedTab;
        set => SetProperty(ref _selectedTab, value);
    }

    public RelayCommand NewTabCommand { get; }
    public RelayCommand CloseTabCommand { get; }
    public RelayCommand DuplicateTabCommand { get; }

    public TabViewModel AddTab(string? startPath = null)
    {
        var path = startPath ?? GetDefaultStartPath();
        var tab = new TabViewModel(_dispatcher, path);
        Tabs.Add(tab);
        SelectedTab = tab;
        return tab;
    }

    public TabViewModel DuplicateTab(TabViewModel source)
    {
        var name = source.HasCustomHeader ? source.Header : null;
        var tab = new TabViewModel(_dispatcher, source.LeftPane.CurrentPath, source.RightPane.CurrentPath, name);
        var index = Tabs.IndexOf(source);
        Tabs.Insert(index < 0 ? Tabs.Count : index + 1, tab);
        SelectedTab = tab;
        return tab;
    }

    private void RestoreSessionOrDefault()
    {
        var saved = SessionService.Load();
        if (saved.Count == 0)
        {
            AddTab();
            return;
        }

        foreach (var state in saved)
        {
            Tabs.Add(new TabViewModel(_dispatcher, state.LeftPath, state.RightPath, state.Name));
        }

        SelectedTab = Tabs[0];
    }

    public void SaveSession()
    {
        SessionService.Save(Tabs.Select(t => new TabState(
            t.LeftPane.CurrentPath, t.RightPane.CurrentPath, t.HasCustomHeader ? t.Header : null)));
    }

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

    private static string GetDefaultStartPath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Directory.Exists(userProfile) ? userProfile : Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
    }
}
