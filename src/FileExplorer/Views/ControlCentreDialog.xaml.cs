using FileExplorer.Helpers;
using FileExplorer.Services;
using FileExplorer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FileExplorer.Views;

public sealed partial class ControlCentreDialog : UserControl
{
    private sealed record SyncTaskRow(string Id, string Name, string PathsDisplay, bool IncludeHiddenSystemFiles);

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
            LoadAboutTileImages();

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
            .Select(t => new SyncTaskRow(t.Id, t.Name, $"{t.SourcePath} → {t.TargetPath}", t.IncludeHiddenSystemFiles))
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

    // ControlCentreDialog only ever runs hosted in its own ContentDialog (see OpenControlCentreAsync
    // in MainWindow), so a nested ContentDialog.ShowAsync() from in here throws ("Only a single
    // ContentDialog can be open at any time"). Flyout has no such restriction.
    private void IncludeHiddenSystemFilesCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: string id } checkBox)
        {
            SyncTaskService.SetIncludeHiddenSystemFiles(id, checkBox.IsChecked == true);
        }
    }

    private void RenameSyncTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SyncTaskRow row } button)
        {
            return;
        }

        var nameBox = new TextBox { Text = row.Name, SelectionStart = 0, SelectionLength = row.Name.Length, Width = 220 };
        var confirmButton = new Button { Content = "Rename", HorizontalAlignment = HorizontalAlignment.Right };
        var flyout = new Flyout { Placement = FlyoutPlacementMode.Bottom };

        void Confirm()
        {
            var newName = nameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(newName))
            {
                flyout.Hide();
                return;
            }

            // Sync tasks are referenced elsewhere by Id, not Name, so a running/scheduled task
            // keeps working under its new display name with no further updates needed.
            SyncTaskService.RenameTask(row.Id, newName);
            flyout.Hide();
        }

        confirmButton.Click += (_, _) => Confirm();
        nameBox.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Enter)
            {
                Confirm();
            }
        };

        flyout.Content = new StackPanel { Spacing = 8, Width = 240, Children = { nameBox, confirmButton } };
        flyout.ShowAt(button);
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

    /// Unpackaged app - loaded via a file stream + SetSourceAsync (same proven pattern
    /// PreviewPaneuses for image previews), rather than `new BitmapImage(new Uri(path))`, which
    /// silently failed to render here for reasons not root-caused.
    private async void LoadAboutTileImages()
    {
        var aboutDir = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "About");

        DualPaneImage.Source = await LoadAboutImageAsync(aboutDir, "dual-pane.png");
        ScriptingImage.Source = await LoadAboutImageAsync(aboutDir, "scripting.png");
        WatchingImage.Source = await LoadAboutImageAsync(aboutDir, "watching.png");
        SyncImage.Source = await LoadAboutImageAsync(aboutDir, "sync.png");
        AnalyserImage.Source = await LoadAboutImageAsync(aboutDir, "analyser.png");
        BenchmarkImage.Source = await LoadAboutImageAsync(aboutDir, "benchmark.png");
    }

    private static async Task<Microsoft.UI.Xaml.Media.Imaging.BitmapImage?> LoadAboutImageAsync(string aboutDir, string fileName)
    {
        var path = System.IO.Path.Combine(aboutDir, fileName);

        try
        {
            using var stream = File.OpenRead(path);
            using var memStream = new MemoryStream();
            await stream.CopyToAsync(memStream);

            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            using var bytesStream = new MemoryStream(memStream.ToArray());
            await bitmap.SetSourceAsync(bytesStream.AsRandomAccessStream());
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.LogWarning($"ControlCentreDialog.LoadAboutImageAsync: {path}", ex);
            return null;
        }
    }

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
