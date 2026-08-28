using FileExplorer.Helpers;
using FileExplorer.Models;
using FileExplorer.Services;
using FileExplorer.ViewModels;
using FileExplorer.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;

namespace FileExplorer;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IFileSystemService _fileSystemService;
    private readonly ISessionService _sessionService;
    private readonly IRemoteConnectionService _remoteConnectionService;

    public MainWindow(IFileSystemService fileSystemService, ISessionService sessionService, IRemoteConnectionService remoteConnectionService)
    {
        InitializeComponent();

        _fileSystemService = fileSystemService;
        _sessionService = sessionService;
        _remoteConnectionService = remoteConnectionService;

        Title = "enfyl Explorer";
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        AppWindow.SetIcon(iconPath);
        TitleBarIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath));

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ConfigureTitleBarButtons();
        RootGrid.ActualThemeChanged += (_, _) => ConfigureTitleBarButtons();

        _viewModel = new MainViewModel(DispatcherQueue, _fileSystemService, _sessionService, _remoteConnectionService);
        RootGrid.DataContext = _viewModel;

        _viewModel.PropertyChanged += ViewModel_PropertyChanged;

        PopulateDriveTree();
        PopulateSavedSearches();
        PopulateNetworkLocations();
        PopulateRemoteConnections();
        PopulateCloudLocations();
        PopulateFavourites();
        FavouriteService.Changed += (_, _) => DispatcherQueue.TryEnqueue(PopulateFavourites);
        SubscribeToActiveTab(_viewModel.SelectedTab);
        SubscribeSyncDropdown(_viewModel.SelectedTab);
        SyncTaskService.Changed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            _viewModel.RefreshAllPanes();
            RefreshSyncDropdown();
        });
        WatchService.Changed += (_, _) => DispatcherQueue.TryEnqueue(_viewModel.RefreshAllPanes);
        WatchService.Triggered += (_, e) => DispatcherQueue.TryEnqueue(() => _ = RunWatchTriggerAsync(e.Task, e.AddedPaths));
        ScheduleService.Due += (_, schedule) => DispatcherQueue.TryEnqueue(() => _ = RunScheduleAsync(schedule));
        SettingsService.Changed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            ApplyFeatureVisibility();
            _viewModel.RefreshAllPanes();

            if (SettingsService.Current.EnableSearchIndex)
            {
                SearchIndexService.Start();
            }
        });

        _ = new ColumnSplitterController(RailSplitter, RailColumn, invert: false, min: 180, max: 480);
        _ = new ColumnSplitterController(PreviewSplitter, PreviewColumn, invert: true, min: 240, max: 600);

        var savedLayout = LayoutSettingsService.Load();

        _previewExpandedWidth = savedLayout.PreviewWidth ?? PreviewColumn.ActualWidth;
        PreviewColumn.Width = new GridLength(_previewExpandedWidth);
        SetPreviewVisible(savedLayout.PreviewOpen);

        if (savedLayout.RailWidth is { } railWidth)
        {
            RailColumn.Width = new GridLength(railWidth);
        }

        TerminalToggleButton.IsChecked = savedLayout.TerminalOpen;
        TerminalRow.Height = savedLayout.TerminalOpen ? new GridLength(260) : new GridLength(0);
        ApplyFeatureVisibility();

        _operationQueue = new FileOperationQueueService(DispatcherQueue, () => Content.XamlRoot);
        _operationQueue.JobCompleted += (_, job) =>
        {
            _viewModel.RefreshAllPanes();
            UpdateOperationsSpinner();

            if (job.Kind == FileDropOperation.Sync)
            {
                if (job.Status == FileOperationStatus.Completed)
                {
                    NotificationService.Show("Sync complete", $"Sync task '{job.SyncTaskName}' has completed.");
                }
                else if (job.Status == FileOperationStatus.Failed)
                {
                    NotificationService.Show("Sync failed", $"Sync task '{job.SyncTaskName}' failed: {job.ErrorMessage}");
                }
            }
        };
        _operationQueue.Jobs.CollectionChanged += (_, _) => UpdateOperationsSpinner();
        OperationsList.ItemsSource = _operationQueue.Jobs;

        UndoService.Instance.Changed += (_, _) => DispatcherQueue.TryEnqueue(() => UndoButton.IsEnabled = UndoService.Instance.CanUndo);

        // Picks up changes made outside the app (another program writing files, a sync tool, etc.)
        // the moment the user comes back to it, same as switching Workspace tabs already does -
        // without this, disk state can silently drift from what's shown until a manual refresh.
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                _viewModel.SelectedTab?.RefreshBoth();
            }
        };

        Closed += (_, _) =>
        {
            _viewModel.SaveSession();
            var width = PreviewColumn.ActualWidth > 0 ? PreviewColumn.ActualWidth : _previewExpandedWidth;
            var railWidth = RailColumn.ActualWidth > 0 ? RailColumn.ActualWidth : (double?)null;
            LayoutSettingsService.Save(new LayoutState(width, TerminalToggleButton.IsChecked == true, PreviewToggleButton.IsChecked == true, railWidth));
        };

        _ = RailDiskActivityLoopAsync();
        _ = PrewarmFrequentFoldersAsync();

        if (SettingsService.Current.EnableSearchIndex)
        {
            SearchIndexService.Start();
        }
    }

    private const int PrewarmFolderCount = 8;

    /// Best-effort background population of FileSystemService's listing cache for whichever local
    /// folders FolderVisitService says the user visits most, so the first navigation into one of
    /// them after launch is instant instead of paying the initial disk-enumeration cost. Never
    /// touches UI state - if a pane loads one of these folders for real before this reaches it,
    /// GetItemsAsync just serves/refreshes the same cache entry either way.
    private async Task PrewarmFrequentFoldersAsync()
    {
        if (!SettingsService.Current.EnableFolderListingCache)
        {
            return;
        }

        foreach (var path in FolderVisitService.GetTopFolders(PrewarmFolderCount))
        {
            if (!Directory.Exists(path))
            {
                continue;
            }

            try
            {
                await _fileSystemService.GetItemsAsync(path, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogWarning($"MainWindow.PrewarmFrequentFoldersAsync: {path}", ex);
            }
        }
    }

    private readonly FileOperationQueueService _operationQueue;
    private double _previewExpandedWidth = 300;

    /// Hides the toolbar surface for a disabled feature (Preferences, in Control Centre). Context
    /// menu entries and command palette entries are gated separately, at the point they're built.
    private void ApplyFeatureVisibility()
    {
        var settings = SettingsService.Current;

        TerminalToggleButton.Visibility = settings.EnableTerminal ? Visibility.Visible : Visibility.Collapsed;
        if (!settings.EnableTerminal && TerminalToggleButton.IsChecked == true)
        {
            TerminalToggleButton.IsChecked = false;
            TerminalRow.Height = new GridLength(0);
        }

        SyncButton.Visibility = settings.EnableSyncTasks ? Visibility.Visible : Visibility.Collapsed;
    }

    // Matches the caption buttons to the app's own Mica/theme colors instead of the OS default
    // white/black block, so the extended title bar reads as part of the app surface.
    private void ConfigureTitleBarButtons()
    {
        var titleBar = AppWindow.TitleBar;
        var isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        var glyphColor = isDark ? Windows.UI.Color.FromArgb(255, 255, 255, 255) : Windows.UI.Color.FromArgb(255, 0, 0, 0);
        var hoverColor = isDark ? Windows.UI.Color.FromArgb(25, 255, 255, 255) : Windows.UI.Color.FromArgb(15, 0, 0, 0);
        var pressedColor = isDark ? Windows.UI.Color.FromArgb(40, 255, 255, 255) : Windows.UI.Color.FromArgb(25, 0, 0, 0);

        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonForegroundColor = glyphColor;
        titleBar.ButtonHoverBackgroundColor = hoverColor;
        titleBar.ButtonHoverForegroundColor = glyphColor;
        titleBar.ButtonPressedBackgroundColor = pressedColor;
        titleBar.ButtonPressedForegroundColor = glyphColor;
    }

    // Tracks running scripts (folder-watch triggers, interval schedules, manual "Run Script")
    // alongside _operationQueue.Jobs so the same gear spins for those too, not just copy/move/sync
    // jobs - a script run doesn't produce a FileOperationQueueService job, so it needs its own
    // counter rather than piggybacking on Jobs.CollectionChanged.
    private int _scriptRunsInProgress;

    private void BeginScriptRun()
    {
        _scriptRunsInProgress++;
        UpdateOperationsSpinner();
    }

    private void EndScriptRun()
    {
        _scriptRunsInProgress--;
        UpdateOperationsSpinner();
    }

    private void UpdateOperationsSpinner()
    {
        var spin = (Storyboard)RootGrid.Resources["OperationsGearSpin"];
        var inProgress = _scriptRunsInProgress > 0 ||
            _operationQueue.Jobs.Any(j => j.Status is FileOperationStatus.Queued or FileOperationStatus.Running);
        var spinning = spin.GetCurrentState() == ClockState.Active;

        if (inProgress && !spinning)
        {
            spin.Begin();
        }
        else if (!inProgress && spinning)
        {
            spin.Stop();
            OperationsGearRotation.Angle = 0;
        }
    }

    private void PaneSplitter_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Rectangle splitter && splitter.Parent is Grid grid && grid.ColumnDefinitions.Count >= 3)
        {
            _ = new ColumnSplitterController(splitter, grid.ColumnDefinitions[0], invert: false, min: 200, max: 4000);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedTab))
        {
            SubscribeToActiveTab(_viewModel.SelectedTab);
            SubscribeSyncDropdown(_viewModel.SelectedTab);
            _viewModel.SelectedTab?.RefreshBoth();
        }
    }

    // ----- Sync tasks toolbar dropdown -----

    private TabViewModel? _syncSubscribedTab;

    private void SubscribeSyncDropdown(TabViewModel? tab)
    {
        if (_syncSubscribedTab is not null)
        {
            _syncSubscribedTab.LeftPane.PathChanged -= SyncRelevantPathChanged;
            _syncSubscribedTab.RightPane.PathChanged -= SyncRelevantPathChanged;
        }

        _syncSubscribedTab = tab;

        if (_syncSubscribedTab is not null)
        {
            _syncSubscribedTab.LeftPane.PathChanged += SyncRelevantPathChanged;
            _syncSubscribedTab.RightPane.PathChanged += SyncRelevantPathChanged;
        }

        RefreshSyncDropdown();
    }

    private void SyncRelevantPathChanged(object? sender, EventArgs e) => RefreshSyncDropdown();

    private void RefreshSyncDropdown()
    {
        var tab = _viewModel.SelectedTab;
        var visibleTasks = tab is null
            ? new List<SyncTaskState>()
            : SyncTaskService.Tasks
                .Where(t => IsVisibleInPane(t.SourcePath, tab.LeftPane.CurrentPath) ||
                            IsVisibleInPane(t.SourcePath, tab.RightPane.CurrentPath))
                .ToList();

        SyncTasksList.ItemsSource = visibleTasks;
        SyncTasksList.Visibility = visibleTasks.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SyncTasksEmptyText.Visibility = visibleTasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SyncTasksList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SyncTaskState task)
        {
            FileOperationQueueService.Current?.EnqueueSync(task);
            SyncButton.Flyout.Hide();
        }
    }

    private async void DeleteSyncTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SyncTaskState task })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Delete Sync Task",
            Content = $"Delete the sync task \"{task.Name}\"? This won't touch any files, just the saved task.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            SyncTaskService.RemoveTask(task.Id);
        }
    }

    /// True when sourcePath is itself an item listed inside the folder currently browsed by the
    /// pane (paneCurrentPath) - i.e. sourcePath's parent directory is paneCurrentPath.
    private static bool IsVisibleInPane(string sourcePath, string paneCurrentPath)
    {
        var parent = System.IO.Path.GetDirectoryName(sourcePath.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        return parent is not null && string.Equals(parent, paneCurrentPath.TrimEnd(System.IO.Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    // Toolbar view-mode buttons are plain ToggleButtons (not a mutually-exclusive RadioButtons
    // group), so IsChecked is driven explicitly here instead of via data binding - clicking one
    // ToggleButton sets a local IsChecked value that a one-way {Binding} on the others wouldn't
    // reliably clear, leaving multiple buttons stuck showing "checked" at once.
    private TabViewModel? _subscribedTab;
    private PaneViewModel? _subscribedPane;

    private void SubscribeToActiveTab(TabViewModel? tab)
    {
        if (_subscribedTab is not null)
        {
            _subscribedTab.PropertyChanged -= SubscribedTab_PropertyChanged;
        }

        _subscribedTab = tab;

        if (_subscribedTab is not null)
        {
            _subscribedTab.PropertyChanged += SubscribedTab_PropertyChanged;
        }

        SubscribeToActivePane(tab?.ActivePane);
    }

    private void SubscribedTab_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.ActivePane))
        {
            SubscribeToActivePane(_subscribedTab?.ActivePane);
        }
    }

    private void SubscribeToActivePane(PaneViewModel? pane)
    {
        if (_subscribedPane is not null)
        {
            _subscribedPane.PropertyChanged -= SubscribedPane_PropertyChanged;
        }

        _subscribedPane = pane;

        if (_subscribedPane is not null)
        {
            _subscribedPane.PropertyChanged += SubscribedPane_PropertyChanged;
        }

        RefreshViewModeButtons();
        UpdatePreview();
    }

    private void SubscribedPane_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PaneViewModel.ViewMode))
        {
            RefreshViewModeButtons();
        }
    }

    private void RefreshViewModeButtons()
    {
        var mode = _subscribedPane?.ViewMode;
        IconsModeButton.IsChecked = mode == ViewMode.Icons;
        ListModeButton.IsChecked = mode == ViewMode.List;
        DetailsModeButton.IsChecked = mode == ViewMode.Details;
        GalleryModeButton.IsChecked = mode == ViewMode.Gallery;
    }

    private void UpdatePreview()
    {
        Preview.ViewModel = _viewModel.SelectedTab?.ActivePane;
    }

    // ----- Drive / folder tree (left rail) -----

    private void PopulateDriveTree()
    {
        DriveTree.RootNodes.Clear();

        foreach (var drive in _fileSystemService.GetReadyDrives())
        {
            var label = string.IsNullOrEmpty(drive.VolumeLabel) ? drive.Name : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";

            double? usedPercent = null;
            string? usageText = null;
            try
            {
                var used = drive.TotalSize - drive.TotalFreeSpace;
                usedPercent = drive.TotalSize > 0 ? used * 100.0 / drive.TotalSize : 0;
                usageText = $"{FormatBytes(used)} of {FormatBytes(drive.TotalSize)} used ({usedPercent:F0}%)";
            }
            catch (IOException)
            {
                // usage unavailable (e.g. some removable media) - bar stays hidden
            }

            var node = new TreeViewNode
            {
                Content = new FolderNode
                {
                    Name = label,
                    FullPath = drive.RootDirectory.FullName,
                    IsDrive = true,
                    IsNetwork = drive.DriveType == DriveType.Network,
                    UsedPercent = usedPercent,
                    UsageText = usageText,
                },
                HasUnrealizedChildren = _fileSystemService.HasSubdirectories(drive.RootDirectory.FullName),
            };
            DriveTree.RootNodes.Add(node);
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:F1} {units[unit]}";
    }

    private void DriveTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        var node = args.Node;
        if (!node.HasUnrealizedChildren || node.Children.Count > 0)
        {
            return;
        }

        node.HasUnrealizedChildren = false;

        if (node.Content is not FolderNode folder)
        {
            return;
        }

        foreach (var child in _fileSystemService.GetSubfolderNodes(folder.FullPath))
        {
            var childNode = new TreeViewNode
            {
                Content = child,
                HasUnrealizedChildren = _fileSystemService.HasSubdirectories(child.FullPath),
            };
            node.Children.Add(childNode);
        }
    }

    private void DriveTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        var folder = args.InvokedItem switch
        {
            FolderNode direct => direct,
            TreeViewNode node => node.Content as FolderNode,
            _ => null,
        };

        if (folder is not null && _viewModel.SelectedTab is { } tab)
        {
            tab.ActivePane.NavigateTo(folder.FullPath);
        }
    }

    private void OpenDriveInNewWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TreeViewNode { Content: FolderNode { IsDrive: true } folder })
        {
            return;
        }

        _viewModel.AddTab(folder.FullPath, MainViewModel.GetDefaultStartPath());
    }

    private void AnalyseDiskMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TreeViewNode { Content: FolderNode { IsDrive: true } folder })
        {
            return;
        }

        _ = OpenDiskSpaceAnalyserAsync(folder.FullPath);
    }

    private void BenchmarkDiskMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TreeViewNode { Content: FolderNode { IsDrive: true } folder })
        {
            return;
        }

        _ = OpenDiskBenchmarkAsync(folder.FullPath);
    }

    // ----- Collapsible left-rail sections -----

    private static void ToggleSection(FontIcon chevron, UIElement content)
    {
        var expanded = content.Visibility == Visibility.Visible;
        content.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        chevron.Glyph = expanded ? "" : ""; // collapsed: chevron up, expanded: chevron down
    }

    private void SavedSearchesHeader_Tapped(object sender, TappedRoutedEventArgs e) =>
        ToggleSection(SavedSearchesChevron, SavedSearchesList);

    private void NetworkLocationsHeader_Tapped(object sender, TappedRoutedEventArgs e) =>
        ToggleSection(NetworkLocationsChevron, NetworkLocationsList);

    private void CloudLocationsHeader_Tapped(object sender, TappedRoutedEventArgs e) =>
        ToggleSection(CloudLocationsChevron, CloudLocationsList);

    private void RemoteConnectionsHeader_Tapped(object sender, TappedRoutedEventArgs e) =>
        ToggleSection(RemoteConnectionsChevron, RemoteConnectionsList);

    private void FavouritesHeader_Tapped(object sender, TappedRoutedEventArgs e) =>
        ToggleSection(FavouritesChevron, FavouritesList);

    private void DiskActivityHeader_Tapped(object sender, TappedRoutedEventArgs e) =>
        ToggleSection(DiskActivityChevron, RailDiskActivityHost);

    // ----- Favourites (left rail) -----

    private void PopulateFavourites()
    {
        // ToList() so this is always a fresh reference - FavouriteService.Load() returns the same
        // cached list instance across calls, and re-assigning ItemsSource to an unchanged reference
        // is a no-op for WinUI's binding (it only rebinds when the reference itself changes).
        FavouritesList.ItemsSource = FavouriteService.Load().ToList();
    }

    private void AddFavouriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedTab?.ActivePane is { } pane)
        {
            AddFavourite(pane.CurrentPath);
        }
    }

    private void FavouritesList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FavouriteLocation favourite || _viewModel.SelectedTab is not { } tab)
        {
            return;
        }

        tab.ActivePane.NavigateTo(favourite.Path);
    }

    private void RemoveFavouriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FavouriteLocation favourite })
        {
            FavouriteService.Remove(favourite);
        }
    }

    private static void AddFavourite(string path)
    {
        if (FavouriteService.IsFavourite(path))
        {
            return;
        }

        var name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        FavouriteService.Add(new FavouriteLocation(string.IsNullOrEmpty(name) ? path : name, path));
    }

    // ----- Saved searches (left rail) -----

    private void PopulateSavedSearches()
    {
        SavedSearchesList.ItemsSource = SavedSearchService.Load();
    }

    private async void SaveCurrentSearchButton_Click(object sender, RoutedEventArgs e)
    {
        var pane = _viewModel.SelectedTab?.ActivePane;
        if (pane is null || string.IsNullOrWhiteSpace(pane.SearchText))
        {
            return;
        }

        var nameBox = new TextBox { PlaceholderText = "Search name", Text = pane.SearchText };
        var dialog = new ContentDialog
        {
            Title = "Save Search",
            Content = nameBox,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(nameBox.Text) ? pane.SearchText : nameBox.Text.Trim();
        SavedSearchService.Add(new SavedSearch(name, pane.CurrentPath, pane.SearchText));
        PopulateSavedSearches();
    }

    private void SavedSearchesList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SavedSearch search)
        {
            return;
        }

        RunSavedSearch(search);
    }

    private void RunSavedSearch(SavedSearch search)
    {
        var pane = _viewModel.SelectedTab?.ActivePane;
        if (pane is null || !Directory.Exists(search.RootPath))
        {
            return;
        }

        pane.NavigateTo(search.RootPath);
        pane.IsRecursiveSearch = true;
        pane.SearchText = search.Query;
    }

    private void RemoveSavedSearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SavedSearch search })
        {
            SavedSearchService.Remove(search);
            PopulateSavedSearches();
        }
    }

    // ----- Network locations (left rail) -----

    private void PopulateNetworkLocations()
    {
        NetworkLocationsList.ItemsSource = NetworkLocationService.Load();
    }

    private async void AddNetworkLocationButton_Click(object sender, RoutedEventArgs e)
    {
        var pathBox = new TextBox { PlaceholderText = @"\\server\share" };
        var nameBox = new TextBox { PlaceholderText = "Display name (optional)" };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(pathBox);
        panel.Children.Add(nameBox);

        var dialog = new ContentDialog
        {
            Title = "Add Network Location",
            Content = panel,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var path = pathBox.Text.Trim();
        if (!path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(nameBox.Text) ? path : nameBox.Text.Trim();
        NetworkLocationService.Add(new NetworkLocation(name, path));
        PopulateNetworkLocations();
    }

    private void NetworkLocationsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not NetworkLocation location || _viewModel.SelectedTab is not { } tab)
        {
            return;
        }

        tab.ActivePane.NavigateTo(location.UncPath);
    }

    private void RemoveNetworkLocationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: NetworkLocation location })
        {
            NetworkLocationService.Remove(location);
            PopulateNetworkLocations();
        }
    }

    private async void MapNetworkDriveButton_Click(object sender, RoutedEventArgs e)
    {
        var mappedPanel = new StackPanel { Spacing = 4 };
        var mappedEmptyText = new TextBlock { Text = "No mapped network drives", Opacity = 0.6, FontSize = 12 };

        void RefreshMapped()
        {
            mappedPanel.Children.Clear();
            var networkDrives = _fileSystemService.GetReadyDrives().Where(d => d.DriveType == DriveType.Network).ToList();
            mappedEmptyText.Visibility = networkDrives.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var drive in networkDrives)
            {
                var letter = drive.Name.TrimEnd('\\', ':')[0];

                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var label = new TextBlock
                {
                    Text = string.IsNullOrEmpty(drive.VolumeLabel) ? drive.Name : $"{drive.Name} ({drive.VolumeLabel})",
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Grid.SetColumn(label, 0);

                var disconnectButton = new Button { Content = "Disconnect" };
                Grid.SetColumn(disconnectButton, 1);
                disconnectButton.Click += (_, _) =>
                {
                    var result = NetworkDriveService.DisconnectDrive(letter);
                    if (result.Success)
                    {
                        PopulateDriveTree();
                        RefreshMapped();
                    }
                };

                row.Children.Add(label);
                row.Children.Add(disconnectButton);
                mappedPanel.Children.Add(row);
            }
        }

        var letterBox = new ComboBox { PlaceholderText = "Drive letter", HorizontalAlignment = HorizontalAlignment.Stretch };

        void RefreshLetters()
        {
            var used = new HashSet<char>(DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));
            letterBox.ItemsSource = Enumerable.Range('D', 'Z' - 'D' + 1)
                .Select(i => (char)i)
                .Where(c => !used.Contains(c))
                .Select(c => $"{c}:")
                .ToList();
            letterBox.SelectedIndex = 0;
        }

        var uncBox = new TextBox { PlaceholderText = @"\\server\share" };
        var credentialsCheck = new CheckBox { Content = "Connect using different credentials" };
        var usernameBox = new TextBox { PlaceholderText = "Username", Visibility = Visibility.Collapsed };
        var passwordBox = new PasswordBox { PlaceholderText = "Password", Visibility = Visibility.Collapsed };
        credentialsCheck.Checked += (_, _) => usernameBox.Visibility = passwordBox.Visibility = Visibility.Visible;
        credentialsCheck.Unchecked += (_, _) => usernameBox.Visibility = passwordBox.Visibility = Visibility.Collapsed;
        var reconnectCheck = new CheckBox { Content = "Reconnect at sign-in", IsChecked = true };

        var addErrorText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 232, 17, 35)),
            Visibility = Visibility.Collapsed,
        };

        var mapButton = new Button { Content = "Map Drive", HorizontalAlignment = HorizontalAlignment.Left };
        mapButton.Click += (_, _) =>
        {
            addErrorText.Visibility = Visibility.Collapsed;

            if (letterBox.SelectedItem is not string letterText)
            {
                addErrorText.Text = "Choose a drive letter.";
                addErrorText.Visibility = Visibility.Visible;
                return;
            }

            var uncPath = uncBox.Text.Trim();
            if (!uncPath.StartsWith(@"\\", StringComparison.Ordinal))
            {
                addErrorText.Text = @"UNC path must start with \\server\share.";
                addErrorText.Visibility = Visibility.Visible;
                return;
            }

            var username = credentialsCheck.IsChecked == true && !string.IsNullOrWhiteSpace(usernameBox.Text) ? usernameBox.Text.Trim() : null;
            var password = credentialsCheck.IsChecked == true ? passwordBox.Password : null;

            var result = NetworkDriveService.MapDrive(letterText[0], uncPath, username, password, reconnectCheck.IsChecked == true);
            if (!result.Success)
            {
                addErrorText.Text = result.ErrorMessage ?? "Unknown error.";
                addErrorText.Visibility = Visibility.Visible;
                return;
            }

            uncBox.Text = string.Empty;
            usernameBox.Text = string.Empty;
            passwordBox.Password = string.Empty;
            PopulateDriveTree();
            RefreshMapped();
            RefreshLetters();
        };

        RefreshMapped();
        RefreshLetters();

        var dialog = new ContentDialog
        {
            Title = "Map Network Drive",
            XamlRoot = Content.XamlRoot,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            Content = new StackPanel
            {
                Spacing = 10,
                Width = 380,
                Children =
                {
                    new TextBlock { Text = "Mapped drives", FontSize = 13, Opacity = 0.85 },
                    mappedEmptyText,
                    mappedPanel,
                    new Rectangle { Height = 1, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(60, 128, 128, 128)) },
                    new TextBlock { Text = "Map a new drive", FontSize = 13, Opacity = 0.85 },
                    letterBox,
                    uncBox,
                    credentialsCheck,
                    usernameBox,
                    passwordBox,
                    reconnectCheck,
                    mapButton,
                    addErrorText,
                },
            },
        };

        await dialog.ShowAsync();
    }

    // ----- Remote connections (left rail) -----

    private void PopulateRemoteConnections()
    {
        RemoteConnectionsList.ItemsSource = _remoteConnectionService.Load();
    }

    private async void AddRemoteConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox { PlaceholderText = "Display name" };
        var protocolBox = new ComboBox { ItemsSource = new[] { "SFTP", "FTP", "FTPS" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var hostBox = new TextBox { PlaceholderText = "Host (e.g. ftp.example.com)" };
        var portBox = new NumberBox { Header = "Port", Value = 22, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var usernameBox = new TextBox { PlaceholderText = "Username" };

        protocolBox.SelectionChanged += (_, _) =>
        {
            portBox.Value = protocolBox.SelectedIndex switch { 0 => 22, _ => 21 };
        };

        var dialog = new ContentDialog
        {
            Title = "Add Remote Connection",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    nameBox, protocolBox, hostBox, portBox, usernameBox,
                    new TextBlock
                    {
                        Text = "Password is asked for each time you connect - it's never saved to disk.",
                        Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var host = hostBox.Text.Trim();
        if (string.IsNullOrEmpty(host))
        {
            return;
        }

        var protocol = protocolBox.SelectedIndex switch
        {
            0 => RemoteProtocol.Sftp,
            2 => RemoteProtocol.Ftps,
            _ => RemoteProtocol.Ftp,
        };

        var name = string.IsNullOrWhiteSpace(nameBox.Text) ? host : nameBox.Text.Trim();
        var connection = new RemoteConnection(Guid.NewGuid().ToString(), name, protocol, host, (int)portBox.Value, usernameBox.Text.Trim());

        _remoteConnectionService.Add(connection);
        PopulateRemoteConnections();
    }

    private async void RemoteConnectionsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not RemoteConnection connection || _viewModel.SelectedTab is not { } tab)
        {
            return;
        }

        try
        {
            var result = await RemoteSessionManager.GetOrConnectAsync(
                connection.Id,
                () => ShowRemotePasswordPromptAsync(connection),
                CancellationToken.None);

            if (result.NewHostKeyTrusted)
            {
                NotificationService.Show(
                    "New SSH host trusted",
                    $"First connection to \"{connection.Name}\" ({connection.Host}) - its host key was trusted and pinned for future connects.");
            }

            var scheme = RemotePathService.SchemeFor(connection.Protocol);
            tab.ActivePane.NavigateTo(RemotePathService.BuildRoot(scheme, connection.Id));
        }
        catch (OperationCanceledException)
        {
            // user cancelled the password prompt
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                Title = $"Couldn't connect to \"{connection.Name}\"",
                Content = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "Close",
                XamlRoot = Content.XamlRoot,
            };
            await dialog.ShowAsync();
        }
    }

    private async Task<string?> ShowRemotePasswordPromptAsync(RemoteConnection connection)
    {
        var passwordBox = new PasswordBox { PlaceholderText = "Password" };

        var dialog = new ContentDialog
        {
            Title = $"Connect to \"{connection.Name}\"",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = $"{connection.Username}@{connection.Host}:{connection.Port}", Opacity = 0.7, FontSize = 12 },
                    passwordBox,
                },
            },
            PrimaryButtonText = "Connect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? passwordBox.Password : null;
    }

    private void RemoveRemoteConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RemoteConnection connection })
        {
            _remoteConnectionService.Remove(connection);
            PopulateRemoteConnections();
        }
    }

    // ----- Cloud storage locations (left rail) -----

    private void PopulateCloudLocations()
    {
        CloudLocationsList.ItemsSource = CloudProviderService.DetectLocations();
    }

    private void CloudLocationsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not CloudLocation location || _viewModel.SelectedTab is not { } tab)
        {
            return;
        }

        tab.ActivePane.NavigateTo(location.Path);
    }

    // ----- Tabs -----

    private void MainTabView_AddTabButtonClick(TabView sender, object args)
    {
        _viewModel.NewTabCommand.Execute(null);
    }

    // TabView (bound via TabItemsSource) reorders its own internal item list on drag-and-drop
    // rather than the source collection, so mirror its resulting order back onto _viewModel.Tabs.
    private void MainTabView_TabItemsChanged(TabView sender, Windows.Foundation.Collections.IVectorChangedEventArgs args)
    {
        var newOrder = sender.TabItems.OfType<TabViewModel>().ToList();
        if (newOrder.Count != _viewModel.Tabs.Count)
        {
            return;
        }

        for (var i = 0; i < newOrder.Count; i++)
        {
            var target = newOrder[i];
            if (!ReferenceEquals(_viewModel.Tabs[i], target))
            {
                _viewModel.Tabs.Move(_viewModel.Tabs.IndexOf(target), i);
            }
        }
    }

    private void DuplicateTabMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TabViewModel tab })
        {
            _viewModel.DuplicateTabCommand.Execute(tab);
        }
    }

    private async void RenameTabMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TabViewModel tab })
        {
            await RenameWorkspaceAsync(tab);
        }
    }

    private async void TabViewItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TabViewModel tab })
        {
            e.Handled = true;
            await RenameWorkspaceAsync(tab);
        }
    }

    private async Task RenameWorkspaceAsync(TabViewModel tab)
    {
        var nameBox = new TextBox { Text = tab.Header };
        var dialog = new ContentDialog
        {
            Title = "Rename Workspace",
            Content = nameBox,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        tab.Rename(nameBox.Text);
    }

    // Same custom drag marker PaneView uses to recognize its own item drags (kept as a
    // duplicated literal rather than a cross-file constant for this one comparison).
    private const string InternalDragFormat = "FileExplorer.InternalDrag";
    private DispatcherQueueTimer? _dragToTabTimer;
    private TabViewModel? _dragToTabPending;

    private void TabViewItem_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.DataView.Properties.ContainsKey(InternalDragFormat) || sender is not FrameworkElement { DataContext: TabViewModel tab })
        {
            return;
        }

        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;

        if (ReferenceEquals(tab, _viewModel.SelectedTab))
        {
            return;
        }

        _dragToTabPending = tab;

        _dragToTabTimer ??= DispatcherQueue.CreateTimer();
        _dragToTabTimer.Stop();
        _dragToTabTimer.Interval = TimeSpan.FromMilliseconds(700);
        _dragToTabTimer.IsRepeating = false;
        _dragToTabTimer.Tick -= DragToTabTimer_Tick;
        _dragToTabTimer.Tick += DragToTabTimer_Tick;
        _dragToTabTimer.Start();
    }

    private void DragToTabTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_dragToTabPending is { } pendingTab)
        {
            _viewModel.SelectedTab = pendingTab;
        }
    }

    private void TabViewItem_DragLeave(object sender, DragEventArgs e)
    {
        _dragToTabPending = null;
        _dragToTabTimer?.Stop();
    }

    private void MainTabView_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is TabViewModel tab)
        {
            _viewModel.CloseTabCommand.Execute(tab);
        }
    }

    // ----- Panes -----

    private void PaneView_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not PaneView pane)
        {
            return;
        }

        pane.Activated -= PaneView_Activated;
        pane.Activated += PaneView_Activated;

        pane.FindDuplicatesRequested -= PaneView_FindDuplicatesRequested;
        pane.FindDuplicatesRequested += PaneView_FindDuplicatesRequested;
    }

    private void PaneView_FindDuplicatesRequested(object? sender, string path) => _ = ShowDuplicateFinderAsync(path);

    private void PaneView_Activated(object? sender, EventArgs e)
    {
        if (sender is not PaneView pane || pane.ViewModel is null || _viewModel.SelectedTab is not { } tab)
        {
            return;
        }

        tab.ActivePane = pane.ViewModel;
        _activePaneView = pane;
        Preview.ViewModel = pane.ViewModel;
    }

    /// The live PaneView behind tab.ActivePane, kept in step by PaneView_Activated. F2/F3's actual
    /// implementations live on PaneView (in-place rename UI, a dialog needing its XamlRoot) rather
    /// than being pure ViewModel operations like Delete, so their global accelerators need the View
    /// instance itself, not just the ActivePane ViewModel.
    private PaneView? _activePaneView;

    // ----- View mode toolbar -----

    private void IconsModeButton_Click(object sender, RoutedEventArgs e) => SetViewMode(ViewMode.Icons);

    private void ListModeButton_Click(object sender, RoutedEventArgs e) => SetViewMode(ViewMode.List);

    private void DetailsModeButton_Click(object sender, RoutedEventArgs e) => SetViewMode(ViewMode.Details);

    private void GalleryModeButton_Click(object sender, RoutedEventArgs e) => SetViewMode(ViewMode.Gallery);

    private void SetViewMode(ViewMode mode)
    {
        if (_viewModel.SelectedTab is { } tab)
        {
            tab.ActivePane.ViewMode = mode;
        }
    }

    private void PreviewToggleButton_Click(object sender, RoutedEventArgs e) =>
        SetPreviewVisible((sender as ToggleButton)?.IsChecked == true);

    private void SetPreviewVisible(bool show)
    {
        if (!show && PreviewColumn.ActualWidth > 0)
        {
            _previewExpandedWidth = PreviewColumn.ActualWidth;
        }

        // MinWidth="240" on the column (needed so dragging the splitter can't shrink it below a
        // usable size) also stops Width=0 from actually collapsing it unless MinWidth is cleared too.
        PreviewColumn.MinWidth = show ? 240 : 0;
        PreviewColumn.Width = show ? new GridLength(_previewExpandedWidth) : new GridLength(0);
        PreviewSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Preview.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PreviewToggleButton.IsChecked = show;
    }

    private void TerminalToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var show = (sender as ToggleButton)?.IsChecked == true;
        TerminalRow.Height = show ? new GridLength(260) : new GridLength(0);
    }

    private async void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        await UndoService.Instance.UndoAsync();
        _viewModel.RefreshAllPanes();
    }

    // ----- Cut / copy / paste / new folder -----

    private void CutButton_Click(object sender, RoutedEventArgs e) => SetClipboard(isCut: true);

    private void CopyButton_Click(object sender, RoutedEventArgs e) => SetClipboard(isCut: false);

    private void SetClipboard(bool isCut)
    {
        if (_viewModel.SelectedTab is not { } tab)
        {
            return;
        }

        var pane = tab.ActivePane;
        var items = pane.SelectedItems.Count > 0
            ? pane.SelectedItems
            : pane.SelectedItem is { } single ? new List<FileSystemItem> { single } : new List<FileSystemItem>();

        if (items.Count == 0)
        {
            return;
        }

        FileClipboardService.Instance.Set(items.Select(i => i.FullPath).ToList(), isCut);
    }

    private void DeleteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        => _ = DeleteActiveSelectionAsync(permanent: false, args);

    private void ShiftDeleteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        => _ = DeleteActiveSelectionAsync(permanent: true, args);

    private async Task DeleteActiveSelectionAsync(bool permanent, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_viewModel.SelectedTab is not { } tab)
        {
            return;
        }

        var pane = tab.ActivePane;
        var items = pane.SelectedItems.Count > 0
            ? pane.SelectedItems
            : pane.SelectedItem is { } single ? new List<FileSystemItem> { single } : new List<FileSystemItem>();

        if (items.Count == 0)
        {
            return;
        }

        args.Handled = true;
        await DeleteService.DeleteItemsAsync(items, permanent, Content.XamlRoot, () => pane.Refresh());
    }

    private void RenameAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_activePaneView is not { } pane)
        {
            return;
        }

        args.Handled = true;
        _ = pane.RenameSelectionAsync();
    }

    private void MoveToFolderAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_activePaneView is not { } pane)
        {
            return;
        }

        args.Handled = true;
        _ = pane.MoveSelectionToNewFolderAsync();
    }

    private void RefreshAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_viewModel.SelectedTab is not { } tab)
        {
            return;
        }

        args.Handled = true;
        tab.ActivePane.Refresh();
    }

    private void FindDuplicatesAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_viewModel.SelectedTab is not { } tab)
        {
            return;
        }

        args.Handled = true;
        _ = ShowDuplicateFinderAsync(tab.ActivePane.CurrentPath);
    }

    private void TogglePreviewAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        TogglePreview();
    }

    private void ToggleTerminalAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleTerminal();
    }

    private void ChecksumAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_activePaneView is not { } pane)
        {
            return;
        }

        args.Handled = true;
        _ = pane.ComputeHashesForSelectionAsync();
    }

    private void ControlCentreAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = OpenControlCentreAsync();
    }

    private void NextTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        CycleTab(forward: true);
    }

    private void PreviousTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        CycleTab(forward: false);
    }

    private void CycleTab(bool forward)
    {
        var tabs = _viewModel.Tabs;
        if (tabs.Count < 2 || _viewModel.SelectedTab is not { } current)
        {
            return;
        }

        var index = tabs.IndexOf(current);
        var next = (index + (forward ? 1 : -1) + tabs.Count) % tabs.Count;
        _viewModel.SelectedTab = tabs[next];
    }

    private void NewTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _viewModel.NewTabCommand.Execute(null);
    }

    private void CloseTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_viewModel.SelectedTab is not { } tab)
        {
            return;
        }

        args.Handled = true;
        _viewModel.CloseTabCommand.Execute(tab);
    }

    private void BackAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_viewModel.SelectedTab is not { } tab)
        {
            return;
        }

        args.Handled = true;
        tab.ActivePane.NavigateBack();
    }

    private void ForwardAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_viewModel.SelectedTab is not { } tab)
        {
            return;
        }

        args.Handled = true;
        tab.ActivePane.NavigateForward();
    }

    private void UpAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_viewModel.SelectedTab is not { } tab)
        {
            return;
        }

        args.Handled = true;
        tab.ActivePane.NavigateUp();
    }

    private void IconsViewAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SetViewMode(ViewMode.Icons);
    }

    private void ListViewAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SetViewMode(ViewMode.List);
    }

    private void DetailsViewAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SetViewMode(ViewMode.Details);
    }

    private void GalleryViewAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SetViewMode(ViewMode.Gallery);
    }

    private void PasteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedTab is { } tab)
        {
            FileClipboardService.Instance.PasteInto(tab.ActivePane.CurrentPath);
        }
    }

    private void NewFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedTab is not { } tab)
        {
            return;
        }

        var pane = tab.ActivePane;
        var candidate = FileOperationService.MakeUniqueDestination(System.IO.Path.Combine(pane.CurrentPath, "New folder"));

        try
        {
            Directory.CreateDirectory(candidate);
            UndoService.Instance.Push(new CreateFolderUndo(candidate));
            pane.Refresh(candidate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.LogWarning("MainWindow.NewFolderButton_Click", ex);
        }
    }

    private void CancelJob_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FileOperationJob job })
        {
            _operationQueue.Cancel(job);
        }
    }

    private void Terminal_GoToActiveFolderRequested(object? sender, EventArgs e)
    {
        if (_viewModel.SelectedTab is { } tab)
        {
            var path = tab.ActivePane.CurrentPath;
            Terminal.RunCommand($"Set-Location -LiteralPath \"{path}\"");
        }
    }

    // ----- Command palette -----

    private sealed record PaletteCommand(string Title, string Subtitle, Action Execute);

    private List<PaletteCommand> _paletteCommands = new();

    private void CommandPaletteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenCommandPalette();
    }

    private void CommandPaletteBar_Tapped(object sender, TappedRoutedEventArgs e) => OpenCommandPalette();

    private void OpenCommandPalette()
    {
        _paletteCommands = BuildPaletteCommands();
        CommandPaletteBox.Text = string.Empty;
        CommandPaletteList.ItemsSource = _paletteCommands;

        CommandPalettePopup.XamlRoot = Content.XamlRoot;
        CommandPalettePopup.HorizontalOffset = (RootGrid.ActualWidth - 520) / 2;
        CommandPalettePopup.VerticalOffset = 90;
        CommandPalettePopup.IsOpen = true;

        CommandPaletteBox.Focus(FocusState.Programmatic);
    }

    private List<PaletteCommand> BuildPaletteCommands()
    {
        var tab = _viewModel.SelectedTab;
        var pane = tab?.ActivePane;

        var settings = SettingsService.Current;

        var commands = new List<PaletteCommand>
        {
            new("New Workspace", "Open a new workspace", () => _viewModel.NewTabCommand.Execute(null)),
            new("Duplicate Workspace", "Open a copy of the current workspace", () => { if (tab is not null) _viewModel.DuplicateTabCommand.Execute(tab); }),
            new("Close Workspace", "Close the current workspace", () => { if (tab is not null) _viewModel.CloseTabCommand.Execute(tab); }),
            new("Rename Workspace...", "Give the current workspace a custom name", () => { if (tab is not null) _ = RenameWorkspaceAsync(tab); }),
            new("Icons View", "Switch the active pane to icons", () => SetViewMode(ViewMode.Icons)),
            new("List View", "Switch the active pane to list", () => SetViewMode(ViewMode.List)),
            new("Details View", "Switch the active pane to details", () => SetViewMode(ViewMode.Details)),
            new("Gallery View", "Switch the active pane to a large-thumbnail gallery", () => SetViewMode(ViewMode.Gallery)),
            new("New Folder", "Create a new folder in the active pane", () => NewFolderButton_Click(this, new RoutedEventArgs())),
            new("Toggle Preview Pane", "Show or hide the preview rail", () => TogglePreview()),
            new("Undo", "Undo the last file operation", () => _ = UndoAndRefreshAsync()),
            new("Go Up", "Navigate to the parent folder", () => pane?.NavigateUp()),
            new("Go Back", "Navigate back", () => pane?.NavigateBack()),
            new("Go Forward", "Navigate forward", () => pane?.NavigateForward()),
            new("Refresh", "Reload the active pane's folder", () => pane?.Refresh()),
            new("Control Centre...", "Manage scripts, sync tasks, automation, thumbnails, and preferences", () => _ = OpenControlCentreAsync()),
        };

        if (settings.EnableSearchIndex)
        {
            commands.Add(new PaletteCommand("Search Everywhere...", "Instant substring search across every indexed file and folder (F9)", () => _ = OpenSearchEverywhereAsync()));
        }

        if (settings.EnableTerminal)
        {
            commands.Add(new PaletteCommand("Toggle Terminal", "Show or hide the terminal drawer", () => ToggleTerminal()));
        }

        if (pane is not null)
        {
            commands.Add(new PaletteCommand(
                "Find Duplicate Files...",
                $"Scan {pane.CurrentPath} and its subfolders",
                () => _ = ShowDuplicateFinderAsync(pane.CurrentPath)));

            if (!string.IsNullOrWhiteSpace(pane.SearchText))
            {
                commands.Add(new PaletteCommand(
                    "Save Current Search...",
                    $"Pin \"{pane.SearchText}\" in {pane.CurrentPath}",
                    () => SaveCurrentSearchButton_Click(this, new RoutedEventArgs())));
            }
        }

        foreach (var search in SavedSearchService.Load())
        {
            commands.Add(new PaletteCommand($"Search: {search.Name}", search.RootPath, () => RunSavedSearch(search)));
        }

        if (settings.EnableScripting)
        {
            foreach (var scriptName in ScriptService.List())
            {
                commands.Add(new PaletteCommand($"Run Script: {scriptName}", "", () => _ = RunScriptAsync(scriptName)));
            }
        }

        return commands;
    }

    private async Task OpenControlCentreAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Control Centre",
            XamlRoot = Content.XamlRoot,
        };

        var centre = new ControlCentreDialog
        {
            MainViewModel = _viewModel,
            ActivePane = _viewModel.SelectedTab?.ActivePane,
            RequestClose = () => dialog.Hide(),
            PickFolder = PickFolderAsync,
        };
        dialog.Content = centre;

        // ContentDialog clips its content to a themed default (548x756) unless overridden - the
        // Control Centre needs real width for the embedded Script Manager editor, so raise the cap
        // to fit it comfortably. The dialog has no built-in Close button (see RequestClose above) -
        // ContentDialog stretches a lone footer button across the full width, which looks wrong at
        // this size, so the Control Centre draws its own right-aligned Close button instead.
        dialog.Resources["ContentDialogMaxWidth"] = 1360d;
        dialog.Resources["ContentDialogMaxHeight"] = 900d;

        await dialog.ShowAsync();
    }

    /// This app is unpackaged, so Windows.Storage.Pickers.FolderPicker needs to be initialized
    /// against a real HWND (WindowNative/InitializeWithWindow) or it throws instead of the implicit
    /// association a packaged app gets for free.
    private async Task<string?> PickFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private async Task OpenSearchEverywhereAsync()
    {
        if (!SettingsService.Current.EnableSearchIndex)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Search Everywhere",
            XamlRoot = Content.XamlRoot,
        };

        var search = new SearchEverywhereDialog
        {
            RequestClose = () => dialog.Hide(),
            // Opens a brand-new "Search Results" workspace rather than navigating whatever pane was
            // last active, so existing workspaces/tabs are never disturbed by following a result. A
            // folder result opens *into* itself (selectPath null - nothing to select); a file result
            // opens its containing folder with the file selected.
            NavigateToResult = (targetPath, selectPath) =>
            {
                dialog.Hide();
                var tab = _viewModel.AddNamedTab(targetPath, "Search Results");
                if (selectPath is not null)
                {
                    tab.LeftPane.Refresh(selectPath);
                }
            },
            OpenSearchIndexSettings = () =>
            {
                dialog.Hide();
                _ = OpenControlCentreAsync();
            },
        };
        dialog.Content = search;

        dialog.Resources["ContentDialogMaxWidth"] = 900d;
        dialog.Resources["ContentDialogMaxHeight"] = 700d;

        await dialog.ShowAsync();
    }

    private void SearchEverywhereAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = OpenSearchEverywhereAsync();
    }

    private void DiskSpaceAnalyserButton_Click(object sender, RoutedEventArgs e) => _ = OpenDiskSpaceAnalyserAsync();

    private async Task OpenDiskSpaceAnalyserAsync(string? initialDrivePath = null)
    {
        var dialog = new ContentDialog
        {
            Title = "Disk Space Analyser",
            XamlRoot = Content.XamlRoot,
        };

        var analyser = new DiskSpaceAnalyserDialog { RequestClose = () => dialog.Hide(), InitialDrivePath = initialDrivePath };
        dialog.Content = analyser;

        dialog.Resources["ContentDialogMaxWidth"] = 1360d;
        dialog.Resources["ContentDialogMaxHeight"] = 900d;

        await dialog.ShowAsync();
    }

    private void DiskBenchmarkButton_Click(object sender, RoutedEventArgs e) => _ = OpenDiskBenchmarkAsync();

    private async Task OpenDiskBenchmarkAsync(string? initialDrivePath = null)
    {
        var dialog = new ContentDialog
        {
            Title = "Disk Benchmark",
            XamlRoot = Content.XamlRoot,
        };

        var benchmark = new DiskBenchmarkDialog { RequestClose = () => dialog.Hide(), InitialDrivePath = initialDrivePath };
        dialog.Content = benchmark;

        dialog.Resources["ContentDialogMaxWidth"] = 1360d;
        dialog.Resources["ContentDialogMaxHeight"] = 900d;

        await dialog.ShowAsync();
    }

    private void DiskActivityMonitorButton_Click(object sender, RoutedEventArgs e) => _ = OpenDiskActivityMonitorAsync();

    private async Task OpenDiskActivityMonitorAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Disk Activity Monitor",
            XamlRoot = Content.XamlRoot,
        };

        var monitor = new DiskActivityMonitorDialog { RequestClose = () => dialog.Hide() };
        dialog.Content = monitor;

        dialog.Resources["ContentDialogMaxWidth"] = 1360d;
        dialog.Resources["ContentDialogMaxHeight"] = 900d;

        await dialog.ShowAsync();
    }

    // ----- Left-rail aggregated disk activity indicator -----
    //
    // A glance-able "something is happening" chart, summed across every drive - no numeric readout
    // by design (the full per-drive breakdown with numbers lives in the dialog above). Started once
    // from the constructor and left running for the app's lifetime rather than tied to any
    // Loaded/Unloaded pairing - see DiskActivityMonitorDialog's own history for why: a naive
    // Unloaded-cancels-the-loop hookup is fragile against spurious Unloaded events triggered by a
    // sibling element's own layout churn, and MainWindow itself has no equivalent "this control gets
    // disposed and recreated" lifecycle to guard against here anyway.
    // 240 samples at the 250ms refresh rate = a rolling minute of history, matching the dialog's own.
    private const int RailDiskActivityHistoryLength = 240;
    private static readonly TimeSpan RailDiskActivityRefreshInterval = TimeSpan.FromMilliseconds(250);
    private readonly Queue<double> _railDiskReadHistory = new();
    private readonly Queue<double> _railDiskWriteHistory = new();
    private bool _isRailDiskActivitySampling;

    private async Task RailDiskActivityLoopAsync()
    {
        while (true)
        {
            if (!_isRailDiskActivitySampling)
            {
                _isRailDiskActivitySampling = true;
                try
                {
                    var samples = await Task.Run(DiskActivityMonitorService.Sample);
                    var totalRead = samples.Sum(s => Math.Max(0, s.ReadMBps));
                    var totalWrite = samples.Sum(s => Math.Max(0, s.WriteMBps));

                    if (_railDiskReadHistory.Count >= RailDiskActivityHistoryLength)
                    {
                        _railDiskReadHistory.Dequeue();
                        _railDiskWriteHistory.Dequeue();
                    }

                    _railDiskReadHistory.Enqueue(totalRead);
                    _railDiskWriteHistory.Enqueue(totalWrite);

                    RedrawRailDiskActivity();
                }
                finally
                {
                    _isRailDiskActivitySampling = false;
                }
            }

            await Task.Delay(RailDiskActivityRefreshInterval);
        }
    }

    private void RailDiskActivityHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RailDiskActivityCanvas.Width = e.NewSize.Width;
        RailDiskActivityCanvas.Height = e.NewSize.Height;
        RedrawRailDiskActivity();
    }

    private void RedrawRailDiskActivity()
    {
        var canvas = RailDiskActivityCanvas;
        var width = canvas.Width;
        var height = canvas.Height;
        canvas.Children.Clear();

        if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0 || _railDiskReadHistory.Count < 2)
        {
            return;
        }

        const double padding = 3;
        var plotHeight = height - padding * 2;
        var maxValue = Math.Max(1.0, Math.Max(_railDiskReadHistory.Max(), _railDiskWriteHistory.Max()));

        AddRailDiskActivityLine(canvas, _railDiskReadHistory, width, plotHeight, padding, maxValue,
            Windows.UI.Color.FromArgb(255, 16, 137, 62));
        AddRailDiskActivityLine(canvas, _railDiskWriteHistory, width, plotHeight, padding, maxValue,
            Windows.UI.Color.FromArgb(255, 255, 140, 0));
    }

    private static void AddRailDiskActivityLine(
        Canvas canvas, Queue<double> history, double width, double plotHeight, double padding, double maxValue, Windows.UI.Color color)
    {
        var values = history.ToArray();
        var stepX = width / (RailDiskActivityHistoryLength - 1);
        var startIndex = RailDiskActivityHistoryLength - values.Length;

        var points = new PointCollection();
        for (var i = 0; i < values.Length; i++)
        {
            var x = (startIndex + i) * stepX;
            var y = padding + plotHeight - (values[i] / maxValue * plotHeight);
            points.Add(new Windows.Foundation.Point(x, y));
        }

        canvas.Children.Add(new Polyline
        {
            Points = points,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 1.25,
        });
    }

    private async Task RunScriptAsync(string scriptName)
    {
        if (!SettingsService.Current.EnableScripting)
        {
            return;
        }

        var code = ScriptService.Load(scriptName);
        if (code is null)
        {
            return;
        }

        BeginScriptRun();
        ScriptRunResult result;
        try
        {
            result = await ScriptEngineService.RunAsync(
                code, _viewModel.SelectedTab?.ActivePane, _viewModel, DispatcherQueue, Content.XamlRoot);
        }
        finally
        {
            EndScriptRun();
        }

        if (result.Success)
        {
            var summary = result.Log.Count > 0 ? string.Join(" | ", result.Log.TakeLast(3)) : "Completed.";
            NotificationService.Show($"Script '{scriptName}' finished", summary);
        }
        else
        {
            var dialog = new ContentDialog
            {
                Title = $"Script '{scriptName}' failed",
                Content = new TextBlock { Text = result.Error, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "Close",
                XamlRoot = Content.XamlRoot,
            };

            await dialog.ShowAsync();
        }
    }

    private async Task RunWatchTriggerAsync(WatchTaskState task, IReadOnlyList<string> addedPaths)
    {
        if (!SettingsService.Current.EnableFolderWatching || !SettingsService.Current.EnableScripting)
        {
            return;
        }

        var code = ScriptService.Load(task.ScriptName);
        if (code is null)
        {
            return;
        }

        // Paused for the duration of the run so the script's own writes into the watched folder
        // (e.g. a rename-in-place) can't re-trigger this same watch and loop forever.
        WatchService.PauseWatcher(task.Id);
        BeginScriptRun();
        ScriptRunResult result;
        try
        {
            result = await ScriptEngineService.RunAsync(
                code, _viewModel.SelectedTab?.ActivePane, _viewModel, DispatcherQueue, Content.XamlRoot, addedPaths);
        }
        finally
        {
            EndScriptRun();
            WatchService.ResumeWatcher(task.Id);
        }

        var summary = result.Success
            ? (result.Log.Count > 0 ? string.Join(" | ", result.Log.TakeLast(3)) : "Completed.")
            : $"Error: {result.Error}";

        NotificationService.Show($"Watch '{task.ScriptName}' ({System.IO.Path.GetFileName(task.FolderPath.TrimEnd('\\'))})", summary);
    }

    private async Task RunScheduleAsync(ScheduleState schedule)
    {
        if (schedule.Kind == ScheduleKind.Sync)
        {
            if (!SettingsService.Current.EnableSyncTasks)
            {
                return;
            }

            var task = SyncTaskService.Tasks.FirstOrDefault(t => t.Id == schedule.TargetName);
            if (task is not null)
            {
                _operationQueue.EnqueueSync(task);
            }

            return;
        }

        if (!SettingsService.Current.EnableScripting)
        {
            return;
        }

        var code = ScriptService.Load(schedule.TargetName);
        if (code is null)
        {
            return;
        }

        BeginScriptRun();
        ScriptRunResult result;
        try
        {
            result = await ScriptEngineService.RunAsync(
                code, _viewModel.SelectedTab?.ActivePane, _viewModel, DispatcherQueue, Content.XamlRoot);
        }
        finally
        {
            EndScriptRun();
        }

        var summary = result.Success
            ? (result.Log.Count > 0 ? string.Join(" | ", result.Log.TakeLast(3)) : "Completed.")
            : $"Error: {result.Error}";

        NotificationService.Show($"Schedule '{schedule.TargetName}'", summary);
    }

    private sealed class DuplicateEntry
    {
        public required string Path { get; init; }
        public required int GroupIndex { get; init; }
        public bool IsKeep { get; set; }
        public CheckBox KeepCheckBox { get; set; } = null!;
        public CheckBox DeleteCheckBox { get; set; } = null!;
    }

    private async Task ShowDuplicateFinderAsync(string rootPath)
    {
        var statusText = new TextBlock { Text = $"Scanning {rootPath} ...", TextWrapping = TextWrapping.Wrap };

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Duplicate Files",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            Content = statusText,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 1360d;

        var showTask = dialog.ShowAsync().AsTask();

        List<List<string>> groups;
        try
        {
            groups = await DuplicateFinderService.FindDuplicatesAsync(rootPath, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Falls back to an empty result, which then shows "No duplicate files found." - not
            // technically true when the scan itself failed, so this needs a trail somewhere.
            LoggingService.LogWarning($"MainWindow.ShowDuplicateFinderAsync: {rootPath}", ex);
            groups = new List<List<string>>();
        }

        if (groups.Count == 0)
        {
            statusText.Text = "No duplicate files found.";
            await showTask;
            return;
        }

        var totalRedundant = groups.Sum(g => g.Count - 1);
        var summary = new TextBlock
        {
            Text = $"{groups.Count} duplicate group(s), {totalRedundant} redundant file(s). " +
                   "Check Keep or Delete per file to override the default, then Delete Selected sends every " +
                   "file checked Delete to the Recycle Bin.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.8,
        };

        var entries = new List<DuplicateEntry>();
        var groupPanel = new StackPanel { Spacing = 12 };
        var monoFont = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas");

        for (var gi = 0; gi < groups.Count; gi++)
        {
            var group = groups[gi];
            groupPanel.Children.Add(new TextBlock
            {
                Text = $"Group {gi + 1} ({group.Count} copies):",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });

            for (var i = 0; i < group.Count; i++)
            {
                var entry = new DuplicateEntry { Path = group[i], GroupIndex = gi, IsKeep = i == 0 };

                var keepBox = new CheckBox { Content = "Keep", IsChecked = entry.IsKeep, MinWidth = 66 };
                var deleteBox = new CheckBox { Content = "Delete", IsChecked = !entry.IsKeep, MinWidth = 70 };
                entry.KeepCheckBox = keepBox;
                entry.DeleteCheckBox = deleteBox;

                // Keep/Delete act as a two-way toggle: exactly one is checked per file at all times.
                keepBox.Checked += (_, _) => { entry.IsKeep = true; deleteBox.IsChecked = false; };
                keepBox.Unchecked += (_, _) => { if (deleteBox.IsChecked != true) deleteBox.IsChecked = true; };
                deleteBox.Checked += (_, _) => { entry.IsKeep = false; keepBox.IsChecked = false; };
                deleteBox.Unchecked += (_, _) => { if (keepBox.IsChecked != true) keepBox.IsChecked = true; };

                entries.Add(entry);

                groupPanel.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        keepBox,
                        deleteBox,
                        new TextBlock
                        {
                            Text = entry.Path,
                            TextWrapping = TextWrapping.NoWrap,
                            VerticalAlignment = VerticalAlignment.Center,
                            FontFamily = monoFont,
                            FontSize = 11,
                        },
                    },
                });
            }
        }

        var swapAllButton = new Button
        {
            Content = "Swap All",
            IsEnabled = groups.Any(g => g.Count == 2),
        };
        swapAllButton.Click += (_, _) =>
        {
            foreach (var pair in entries.GroupBy(e => e.GroupIndex).Where(g => g.Count() == 2))
            {
                foreach (var entry in pair)
                {
                    entry.KeepCheckBox.IsChecked = !entry.IsKeep;
                }
            }
        };

        var scrollViewer = new ScrollViewer
        {
            Height = 320,
            Width = 1300,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = groupPanel,
        };

        dialog.Content = new StackPanel { Spacing = 8, Children = { summary, swapAllButton, scrollViewer } };
        dialog.PrimaryButtonText = "Delete Selected";

        var result = await showTask;
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        var toDelete = entries.Where(e => !e.IsKeep).Select(e => e.Path).ToList();
        var progressText = new TextBlock { Text = $"Deleting 0 of {toDelete.Count} ...", TextWrapping = TextWrapping.Wrap };
        var progressDialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Deleting Duplicates",
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Children = { new ProgressRing { IsActive = true, Width = 24, Height = 24 }, progressText },
            },
        };
        var progressShowTask = progressDialog.ShowAsync().AsTask();

        var failures = new List<string>();

        // Each Recycle Bin move is a shell operation and can be slow (especially on removable/USB
        // drives) - running this loop on the UI thread, as this code used to, froze the whole app for
        // the entire batch with no way to tell it apart from a genuine hang.
        await Task.Run(() =>
        {
            var done = 0;
            foreach (var duplicate in toDelete)
            {
                try
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        duplicate,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LoggingService.LogWarning($"MainWindow.ShowDuplicateFinderAsync (delete): {duplicate}", ex);
                    failures.Add(duplicate);
                }

                done++;
                var current = done;
                DispatcherQueue.TryEnqueue(() => progressText.Text = $"Deleting {current} of {toDelete.Count} ...");
            }
        });

        progressDialog.Hide();
        await progressShowTask;

        _viewModel.RefreshAllPanes();

        if (failures.Count > 0)
        {
            var failureDialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = failures.Count == 1 ? "Couldn't delete 1 file" : $"Couldn't delete {failures.Count} files",
                Content = new TextBlock { Text = string.Join("\n", failures), TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "Close",
            };

            await failureDialog.ShowAsync();
        }
    }

    private async Task UndoAndRefreshAsync()
    {
        await UndoService.Instance.UndoAsync();
        _viewModel.RefreshAllPanes();
    }

    private void TogglePreview()
    {
        PreviewToggleButton.IsChecked = !(PreviewToggleButton.IsChecked ?? false);
        PreviewToggleButton_Click(PreviewToggleButton, new RoutedEventArgs());
    }

    private void ToggleTerminal()
    {
        TerminalToggleButton.IsChecked = !(TerminalToggleButton.IsChecked ?? false);
        TerminalToggleButton_Click(TerminalToggleButton, new RoutedEventArgs());
    }

    private void CommandPaletteBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = CommandPaletteBox.Text.Trim();
        IEnumerable<object> results = _paletteCommands;

        if (!string.IsNullOrEmpty(query))
        {
            results = _paletteCommands.Where(c =>
                c.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                c.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase));

            if (Directory.Exists(query))
            {
                var goTo = new PaletteCommand($"Go to \"{query}\"", "Navigate the active pane here", () =>
                {
                    if (_viewModel.SelectedTab is { } t)
                    {
                        t.ActivePane.NavigateTo(query);
                    }
                });
                results = new object[] { goTo }.Concat(results);
            }
        }

        CommandPaletteList.ItemsSource = results.ToList();
    }

    private void CommandPaletteBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            if (CommandPaletteList.Items.Count > 0)
            {
                ExecutePaletteCommand(CommandPaletteList.Items[0]);
            }
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CommandPalettePopup.IsOpen = false;
        }
        else if (e.Key == Windows.System.VirtualKey.Down && CommandPaletteList.Items.Count > 0)
        {
            CommandPaletteList.Focus(FocusState.Programmatic);
            CommandPaletteList.SelectedIndex = 0;
        }
    }

    private void CommandPaletteList_ItemClick(object sender, ItemClickEventArgs e) => ExecutePaletteCommand(e.ClickedItem);

    private void ExecutePaletteCommand(object item)
    {
        if (item is PaletteCommand command)
        {
            CommandPalettePopup.IsOpen = false;
            command.Execute();
        }
    }
}
