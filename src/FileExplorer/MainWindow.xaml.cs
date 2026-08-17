using FileExplorer.Helpers;
using FileExplorer.Models;
using FileExplorer.Services;
using FileExplorer.ViewModels;
using FileExplorer.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Shapes;

namespace FileExplorer;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        Title = "File Explorer";
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        _viewModel = new MainViewModel(DispatcherQueue);
        RootGrid.DataContext = _viewModel;

        _viewModel.PropertyChanged += ViewModel_PropertyChanged;

        PopulateDriveTree();
        UpdatePreview();

        _ = new ColumnSplitterController(RailSplitter, RailColumn, invert: false, min: 180, max: 480);
        _ = new ColumnSplitterController(PreviewSplitter, PreviewColumn, invert: true, min: 240, max: 600);

        _operationQueue = new FileOperationQueueService(DispatcherQueue);
        _operationQueue.JobCompleted += (_, _) => _viewModel.RefreshAllPanes();
        OperationsList.ItemsSource = _operationQueue.Jobs;
    }

    private readonly FileOperationQueueService _operationQueue;
    private List<string>? _clipboardPaths;
    private bool _clipboardIsCut;

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
            UpdatePreview();
        }
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
            var node = new TreeViewNode
            {
                Content = new FolderNode { Name = label, FullPath = drive.RootDirectory.FullName, IsDrive = true },
                HasUnrealizedChildren = FileSystemService.HasSubdirectories(drive.RootDirectory.FullName),
            };
            DriveTree.RootNodes.Add(node);
        }
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

    // ----- Tabs -----

    private void MainTabView_AddTabButtonClick(TabView sender, object args)
    {
        _viewModel.NewTabCommand.Execute(null);
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

    private void SetViewMode(ViewMode mode)
    {
        if (_viewModel.SelectedTab is { } tab)
        {
            tab.ActivePane.ViewMode = mode;
        }
    }

    private void PreviewToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var show = (sender as ToggleButton)?.IsChecked == true;
        PreviewColumn.Width = show ? new GridLength(300) : new GridLength(0);
    }

    private void TerminalToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var show = (sender as ToggleButton)?.IsChecked == true;
        TerminalRow.Height = show ? new GridLength(260) : new GridLength(0);
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

        _clipboardPaths = items.Select(i => i.FullPath).ToList();
        _clipboardIsCut = isCut;
    }

    private void PasteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_clipboardPaths is not { Count: > 0 } || _viewModel.SelectedTab is not { } tab)
        {
            return;
        }

        var destination = tab.ActivePane.CurrentPath;
        if (!FileOperationService.IsValidDropTarget(_clipboardPaths, destination))
        {
            return;
        }

        var op = _clipboardIsCut ? FileDropOperation.Move : FileDropOperation.Copy;
        _operationQueue.Enqueue(_clipboardPaths, destination, op);

        if (_clipboardIsCut)
        {
            _clipboardPaths = null; // cut is a one-shot move
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
}
