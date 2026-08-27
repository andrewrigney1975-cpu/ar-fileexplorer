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

    /// Set by MainWindow (it owns the window handle a folder picker needs to initialize against on
    /// an unpackaged app) - see AddSearchIndexRoot_Click.
    public Func<Task<string?>>? PickFolder { get; set; }

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
            PopulateKeyboardShortcuts();
            RefreshSearchIndex();

            SyncTaskService.Changed += OnSyncTasksChanged;
            SettingsService.Changed += OnSettingsChanged;
            SearchIndexService.StatusChanged += OnSearchIndexStatusChanged;

            SectionList.SelectedItem = ScriptsNavItem;
            ApplyNavVisibility();
        };

        Unloaded += (_, _) =>
        {
            SyncTaskService.Changed -= OnSyncTasksChanged;
            SettingsService.Changed -= OnSettingsChanged;
            SearchIndexService.StatusChanged -= OnSearchIndexStatusChanged;
        };
    }

    private void OnSyncTasksChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(RefreshSyncTasks);

    private void OnSettingsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(ApplyNavVisibility);

    private void OnSearchIndexStatusChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(RefreshSearchIndex);

    private void RefreshSearchIndex()
    {
        var roots = SearchIndexService.Roots;
        SearchIndexRootsList.ItemsSource = roots;
        SearchIndexRootsEmptyText.Visibility = roots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RebuildSearchIndexButton.IsEnabled = roots.Count > 0 && !SearchIndexService.IsScanning;

        SearchIndexStatusText.Text = SearchIndexService.IsScanning
            ? $"Scanning... ({SearchIndexService.EntryCount:N0} entries indexed so far)"
            : SearchIndexService.LastScanUtc is { } lastScan
                ? $"{SearchIndexService.EntryCount:N0} entries indexed. Last full scan: {FileExplorer.Models.FileSystemItem.FormatDate(lastScan.ToLocalTime())}."
                : roots.Count == 0
                    ? "Add a folder or drive below to start indexing."
                    : "Not scanned yet.";
    }

    private async void AddSearchIndexRoot_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder is null)
        {
            return;
        }

        var path = await PickFolder();
        if (!string.IsNullOrEmpty(path))
        {
            SearchIndexService.AddRoot(path);
        }
    }

    private void RemoveSearchIndexRoot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
        {
            SearchIndexService.RemoveRoot(path);
        }
    }

    private void RebuildSearchIndex_Click(object sender, RoutedEventArgs e) => _ = SearchIndexService.RebuildAsync(CancellationToken.None);

    private void Close_Click(object sender, RoutedEventArgs e) => RequestClose?.Invoke();

    private void ApplyNavVisibility()
    {
        var settings = SettingsService.Current;
        ScriptsNavItem.Visibility = settings.EnableScripting ? Visibility.Visible : Visibility.Collapsed;
        SyncTasksNavItem.Visibility = settings.EnableSyncTasks ? Visibility.Visible : Visibility.Collapsed;
        SearchIndexNavItem.Visibility = settings.EnableSearchIndex ? Visibility.Visible : Visibility.Collapsed;

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
        SearchIndexPanel.Visibility = ReferenceEquals(SectionList.SelectedItem, SearchIndexNavItem) ? Visibility.Visible : Visibility.Collapsed;
        if (ReferenceEquals(SectionList.SelectedItem, SearchIndexNavItem))
        {
            RefreshSearchIndex();
        }
        PreferencesPanel.Visibility = ReferenceEquals(SectionList.SelectedItem, PreferencesNavItem) ? Visibility.Visible : Visibility.Collapsed;
        KeyboardShortcutsPanel.Visibility = ReferenceEquals(SectionList.SelectedItem, KeyboardShortcutsNavItem) ? Visibility.Visible : Visibility.Collapsed;
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

    /// Built in code rather than hand-written XAML rows - the two-column layout and the
    /// keep-groups-together column split are both far easier to get right (and re-balance later)
    /// as data than as ~200 lines of repeated Grid/Border/TextBlock markup.
    private void PopulateKeyboardShortcuts()
    {
        // Function keys grouped together and sorted by number, per the user's explicit ask -
        // otherwise they'd be scattered across Files/Navigation/View/Tools by topic.
        var functionKeys = ("FUNCTION KEYS", new[]
        {
            ("F2", "Rename"),
            ("F3", "Move to new folder"),
            ("F4", "Find duplicate files"),
            ("F5", "Refresh"),
            ("F6", "Toggle preview pane"),
            ("F7", "Toggle terminal"),
            ("F8", "Checksum selection"),
            ("F9", "Search Everywhere"),
            ("F10", "Control Centre"),
        });

        var files = ("FILES", new[]
        {
            ("Delete", "Delete to Recycle Bin"),
            ("Shift+Delete", "Delete permanently"),
            ("Ctrl+X", "Cut"),
            ("Ctrl+C", "Copy"),
            ("Ctrl+V", "Paste"),
            ("Ctrl+Shift+N", "New folder"),
            ("Ctrl+Z", "Undo"),
        });

        var navigation = ("NAVIGATION", new[]
        {
            ("Alt+Left", "Back"),
            ("Alt+Right", "Forward"),
            ("Alt+Up", "Up one level"),
        });

        var view = ("VIEW", new[]
        {
            ("Ctrl+Shift+1", "Icons view"),
            ("Ctrl+Shift+2", "List view"),
            ("Ctrl+Shift+3", "Details view"),
            ("Ctrl+Shift+4", "Gallery view"),
        });

        var workspaces = ("WORKSPACES", new[]
        {
            ("Ctrl+T", "New Workspace tab"),
            ("Ctrl+W", "Close Workspace tab"),
            ("Ctrl+Tab", "Next Workspace"),
            ("Ctrl+Shift+Tab", "Previous Workspace"),
        });

        var tools = ("TOOLS", new[]
        {
            ("Ctrl+K", "Command Palette"),
        });

        // Not KeyboardAccelerators - routed KeyDown, so only active while that specific control
        // has literal focus (see the keyboard-shortcut-pattern lesson: Space is deliberately kept
        // routed rather than global so it doesn't hijack every button's activation key).
        var focusScoped = ("WHEN FOCUSED", new[]
        {
            ("Space", "Quick Look preview (file list)"),
            ("Enter", "Navigate typed path / run terminal command"),
            ("Esc", "Revert path box / clear search box"),
            ("Up / Down", "Terminal command history"),
        });

        // Balanced by row count (including each group's own header) across 3 columns, not just by
        // topic - Function Keys is the biggest single group so it gets a column mostly to itself.
        AddShortcutGroup(ShortcutsColumn1, functionKeys);
        AddShortcutGroup(ShortcutsColumn1, tools);

        AddShortcutGroup(ShortcutsColumn2, files);
        AddShortcutGroup(ShortcutsColumn2, navigation);

        AddShortcutGroup(ShortcutsColumn3, view);
        AddShortcutGroup(ShortcutsColumn3, workspaces);
        AddShortcutGroup(ShortcutsColumn3, focusScoped);
    }

    private void AddShortcutGroup(StackPanel column, (string Header, (string Key, string Description)[] Rows) group)
    {
        column.Children.Add(new TextBlock
        {
            Text = group.Header,
            Style = (Style)Resources["ShortcutGroupHeaderStyle"],
        });

        // Key column stays a fixed width (roughly the longest key label, "Ctrl+Shift+Tab") so the
        // chips line up across rows within a group. Description column is Auto, not a fixed
        // width or Star - Auto sizes to that group's own longest description (e.g. TOOLS ends up
        // much narrower than WHEN FOCUSED), which is what actually packs each column tight
        // instead of every row reserving room for the single longest description in the whole
        // panel. Auto is also the safe choice inside a ScrollViewer's unconstrained measure pass;
        // Star is not (see the About panel's own Width-not-MaxWidth comment).
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8, Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        for (var i = 0; i < group.Rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var (key, description) = group.Rows[i];

            var keyBorder = new Border
            {
                Style = (Style)Resources["ShortcutKeyStyle"],
                Child = new TextBlock { Text = key, FontSize = 12 },
            };
            Grid.SetRow(keyBorder, i);
            Grid.SetColumn(keyBorder, 0);
            grid.Children.Add(keyBorder);

            var descText = new TextBlock
            {
                Text = description,
                FontSize = 12,
                Opacity = 0.85,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 230,
            };
            Grid.SetRow(descText, i);
            Grid.SetColumn(descText, 1);
            grid.Children.Add(descText);
        }

        column.Children.Add(grid);
    }

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
        // each of the lines below would fire FeatureToggle_Toggled immediately, which reads
        // *all* switches' current IsOn to build the saved settings. Since the others
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
        FolderListingCacheToggle.IsOn = settings.EnableFolderListingCache;
        SearchIndexToggle.IsOn = settings.EnableSearchIndex;
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
            EnableFolderListingCache = FolderListingCacheToggle.IsOn,
            EnableSearchIndex = SearchIndexToggle.IsOn,
        };

        if (updated != current)
        {
            SettingsService.Update(updated);
        }
    }
}
