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
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;

namespace FileExplorer;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        Title = "arExx Pro";
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ConfigureTitleBarButtons();
        RootGrid.ActualThemeChanged += (_, _) => ConfigureTitleBarButtons();

        _viewModel = new MainViewModel(DispatcherQueue);
        RootGrid.DataContext = _viewModel;

        _viewModel.PropertyChanged += ViewModel_PropertyChanged;

        PopulateDriveTree();
        PopulateSavedSearches();
        PopulateNetworkLocations();
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

        _ = new ColumnSplitterController(RailSplitter, RailColumn, invert: false, min: 180, max: 480);
        _ = new ColumnSplitterController(PreviewSplitter, PreviewColumn, invert: true, min: 240, max: 600);

        var savedLayout = LayoutSettingsService.Load();

        _previewExpandedWidth = savedLayout.PreviewWidth ?? PreviewColumn.ActualWidth;
        PreviewColumn.Width = new GridLength(_previewExpandedWidth);
        SetPreviewVisible(savedLayout.PreviewOpen);

        TerminalToggleButton.IsChecked = savedLayout.TerminalOpen;
        TerminalRow.Height = savedLayout.TerminalOpen ? new GridLength(260) : new GridLength(0);

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

        Closed += (_, _) =>
        {
            _viewModel.SaveSession();
            var width = PreviewColumn.ActualWidth > 0 ? PreviewColumn.ActualWidth : _previewExpandedWidth;
            LayoutSettingsService.Save(new LayoutState(width, TerminalToggleButton.IsChecked == true, PreviewToggleButton.IsChecked == true));
        };
    }

    private readonly FileOperationQueueService _operationQueue;
    private double _previewExpandedWidth = 300;

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

    private void UpdateOperationsSpinner()
    {
        var spin = (Storyboard)RootGrid.Resources["OperationsGearSpin"];
        var inProgress = _operationQueue.Jobs.Any(j => j.Status is FileOperationStatus.Queued or FileOperationStatus.Running);
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

        foreach (var drive in FileSystemService.GetReadyDrives())
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
                HasUnrealizedChildren = FileSystemService.HasSubdirectories(drive.RootDirectory.FullName),
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

        foreach (var child in FileSystemService.GetSubfolderNodes(folder.FullPath))
        {
            var childNode = new TreeViewNode
            {
                Content = child,
                HasUnrealizedChildren = FileSystemService.HasSubdirectories(child.FullPath),
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

    private void FavouritesHeader_Tapped(object sender, TappedRoutedEventArgs e) =>
        ToggleSection(FavouritesChevron, FavouritesList);

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
    }

    private void PaneView_Activated(object? sender, EventArgs e)
    {
        if (sender is not PaneView pane || pane.ViewModel is null || _viewModel.SelectedTab is not { } tab)
        {
            return;
        }

        tab.ActivePane = pane.ViewModel;
        Preview.ViewModel = pane.ViewModel;
    }

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
        var basePath = pane.CurrentPath;
        var candidate = System.IO.Path.Combine(basePath, "New folder");

        for (int i = 2; Directory.Exists(candidate) || File.Exists(candidate); i++)
        {
            candidate = System.IO.Path.Combine(basePath, $"New folder ({i})");
        }

        try
        {
            Directory.CreateDirectory(candidate);
            UndoService.Instance.Push(new CreateFolderUndo(candidate));
            pane.Refresh(candidate);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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
            new("Toggle Terminal", "Show or hide the terminal drawer", () => ToggleTerminal()),
            new("Undo", "Undo the last file operation", () => _ = UndoAndRefreshAsync()),
            new("Go Up", "Navigate to the parent folder", () => pane?.NavigateUp()),
            new("Go Back", "Navigate back", () => pane?.NavigateBack()),
            new("Go Forward", "Navigate forward", () => pane?.NavigateForward()),
            new("Refresh", "Reload the active pane's folder", () => pane?.Refresh()),
            new("Manage Scripts...", "Create, edit, and run scripts", () => _ = OpenScriptManagerAsync()),
        };

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

        foreach (var scriptName in ScriptService.List())
        {
            commands.Add(new PaletteCommand($"Run Script: {scriptName}", "", () => _ = RunScriptAsync(scriptName)));
        }

        return commands;
    }

    private async Task OpenScriptManagerAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Scripts",
            XamlRoot = Content.XamlRoot,
        };

        var manager = new ScriptManagerDialog
        {
            MainViewModel = _viewModel,
            ActivePane = _viewModel.SelectedTab?.ActivePane,
            RequestClose = () => dialog.Hide(),
        };
        dialog.Content = manager;

        // ContentDialog clips its content to a themed default (548x756) unless overridden - the
        // Script Manager needs real width for the editor, so raise the cap to fit it comfortably.
        // The dialog has no built-in Close button (see RequestClose above) - ContentDialog stretches
        // a lone footer button across the full width, which looks wrong at this size, so the Script
        // Manager draws its own right-aligned Close button instead.
        dialog.Resources["ContentDialogMaxWidth"] = 1180d;
        dialog.Resources["ContentDialogMaxHeight"] = 760d;

        await dialog.ShowAsync();
    }

    private async Task RunScriptAsync(string scriptName)
    {
        var code = ScriptService.Load(scriptName);
        if (code is null)
        {
            return;
        }

        var result = await ScriptEngineService.RunAsync(
            code, _viewModel.SelectedTab?.ActivePane, _viewModel, DispatcherQueue, Content.XamlRoot);

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

        var showTask = dialog.ShowAsync().AsTask();

        List<List<string>> groups;
        try
        {
            groups = await DuplicateFinderService.FindDuplicatesAsync(rootPath, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
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
                   "Delete Selected removes every copy except the first (marked [KEEP]) in each group, to the Recycle Bin.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.8,
        };

        var lines = groups.SelectMany((group, gi) =>
            new[] { $"Group {gi + 1} ({group.Count} copies):" }
                .Concat(group.Select((f, i) => (i == 0 ? "  [KEEP] " : "  [DUP]  ") + f)));

        var listBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11,
            Height = 320,
            Text = string.Join("\n", lines),
        };

        dialog.Content = new StackPanel { Spacing = 8, Children = { summary, listBox } };
        dialog.PrimaryButtonText = "Delete Selected";

        var result = await showTask;
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        foreach (var duplicate in groups.SelectMany(g => g.Skip(1)))
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
            }
        }

        _viewModel.RefreshAllPanes();
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
