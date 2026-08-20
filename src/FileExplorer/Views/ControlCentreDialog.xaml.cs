using FileExplorer.Helpers;
using FileExplorer.Services;
using FileExplorer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileExplorer.Views;

public sealed partial class ControlCentreDialog : UserControl
{
    private sealed record SyncTaskRow(string Id, string Name, string PathsDisplay);

    public MainViewModel? MainViewModel { get; set; }

    public PaneViewModel? ActivePane { get; set; }

    public Action? RequestClose { get; set; }

    public ControlCentreDialog()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            var scriptManager = new ScriptManagerDialog { MainViewModel = MainViewModel, ActivePane = ActivePane };
            scriptManager.HideCloseButton();
            ScriptsHost.Children.Add(scriptManager);

            var automation = new AutomationDialog();
            automation.HideCloseButton();
            AutomationHost.Children.Add(automation);

            RefreshSyncTasks();
            LoadPreferences();
            ThumbnailSizeBox.Value = SettingsService.Current.ThumbnailSize;
            AboutVersionText.Text = $"Version {AppVersionInfo.Version}";

            SyncTaskService.Changed += OnSyncTasksChanged;
            SettingsService.Changed += OnSettingsChanged;

            SectionList.SelectedItem = ScriptsNavItem;
            ApplyNavVisibility();
        };

        Unloaded += (_, _) =>
        {
            SyncTaskService.Changed -= OnSyncTasksChanged;
            SettingsService.Changed -= OnSettingsChanged;
        };
    }

    private void OnSyncTasksChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(RefreshSyncTasks);

    private void OnSettingsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(ApplyNavVisibility);

    private void Close_Click(object sender, RoutedEventArgs e) => RequestClose?.Invoke();

    private void ApplyNavVisibility()
    {
        var settings = SettingsService.Current;
        ScriptsNavItem.Visibility = settings.EnableScripting ? Visibility.Visible : Visibility.Collapsed;
        SyncTasksNavItem.Visibility = settings.EnableSyncTasks ? Visibility.Visible : Visibility.Collapsed;

        if (SectionList.SelectedItem is ListViewItem selected && selected.Visibility == Visibility.Collapsed)
        {
            SectionList.SelectedItem = AutomationNavItem;
        }
    }

    private void SectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ScriptsHost.Visibility = ReferenceEquals(SectionList.SelectedItem, ScriptsNavItem) ? Visibility.Visible : Visibility.Collapsed;
        SyncTasksPanel.Visibility = ReferenceEquals(SectionList.SelectedItem, SyncTasksNavItem) ? Visibility.Visible : Visibility.Collapsed;
        AutomationHost.Visibility = ReferenceEquals(SectionList.SelectedItem, AutomationNavItem) ? Visibility.Visible : Visibility.Collapsed;
        ThumbnailsPanel.Visibility = ReferenceEquals(SectionList.SelectedItem, ThumbnailsNavItem) ? Visibility.Visible : Visibility.Collapsed;
        if (ReferenceEquals(SectionList.SelectedItem, ThumbnailsNavItem))
        {
            ThumbnailSizeBox.Value = SettingsService.Current.ThumbnailSize;
        }
        PreferencesPanel.Visibility = ReferenceEquals(SectionList.SelectedItem, PreferencesNavItem) ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = ReferenceEquals(SectionList.SelectedItem, AboutNavItem) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshSyncTasks()
    {
        var rows = SyncTaskService.Tasks
            .Select(t => new SyncTaskRow(t.Id, t.Name, $"{t.SourcePath} → {t.TargetPath}"))
            .ToList();

        SyncTasksList.ItemsSource = rows;
        SyncTasksEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RunSyncTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id } &&
            SyncTaskService.Tasks.FirstOrDefault(t => t.Id == id) is { } task)
        {
            FileOperationQueueService.Current?.EnqueueSync(task);
        }
    }

    private void DeleteSyncTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            SyncTaskService.RemoveTask(id);
        }
    }

    private void SaveThumbnailSize_Click(object sender, RoutedEventArgs e)
    {
        var size = (int)Math.Clamp(ThumbnailSizeBox.Value, ThumbnailSizeBox.Minimum, ThumbnailSizeBox.Maximum);
        var current = SettingsService.Current;
        SettingsService.Update(current with { ThumbnailSize = size });
    }

    private bool _loadingPreferences;

    private void LoadPreferences()
    {
        // Setting IsOn programmatically fires Toggled just like a user click - without this guard,
        // each of the four lines below would fire FeatureToggle_Toggled immediately, which reads
        // *all four* switches' current IsOn to build the saved settings. Since the other three
        // haven't been set yet at that point, it would momentarily persist wrong values (e.g. only
        // the first switch set, the rest still at their default-false) before the last line's fire
        // corrects it - a real bug, not just a cosmetic one, since every intermediate write also
        // raises SettingsService.Changed and briefly hides nav items/toolbar buttons.
        _loadingPreferences = true;
        var settings = SettingsService.Current;
        TerminalToggle.IsOn = settings.EnableTerminal;
        SyncTasksToggle.IsOn = settings.EnableSyncTasks;
        FolderWatchingToggle.IsOn = settings.EnableFolderWatching;
        ScriptingToggle.IsOn = settings.EnableScripting;
        _loadingPreferences = false;
    }

    private void FeatureToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingPreferences)
        {
            return;
        }

        var current = SettingsService.Current;
        var updated = current with
        {
            EnableTerminal = TerminalToggle.IsOn,
            EnableSyncTasks = SyncTasksToggle.IsOn,
            EnableFolderWatching = FolderWatchingToggle.IsOn,
            EnableScripting = ScriptingToggle.IsOn,
        };

        if (updated != current)
        {
            SettingsService.Update(updated);
        }
    }
}
