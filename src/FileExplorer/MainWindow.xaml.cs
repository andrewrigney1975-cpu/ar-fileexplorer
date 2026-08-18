using FileExplorer.Helpers;
using FileExplorer.Models;
using FileExplorer.Services;
using FileExplorer.ViewModels;
using FileExplorer.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
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

        UndoService.Instance.Changed += (_, _) => DispatcherQueue.TryEnqueue(() => UndoButton.IsEnabled = UndoService.Instance.CanUndo);
    }

    private readonly FileOperationQueueService _operationQueue;

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
            new("New Tab", "Open a new tab", () => _viewModel.NewTabCommand.Execute(null)),
            new("Close Tab", "Close the current tab", () => { if (tab is not null) _viewModel.CloseTabCommand.Execute(tab); }),
            new("Icons View", "Switch the active pane to icons", () => SetViewMode(ViewMode.Icons)),
            new("List View", "Switch the active pane to list", () => SetViewMode(ViewMode.List)),
            new("Details View", "Switch the active pane to details", () => SetViewMode(ViewMode.Details)),
            new("New Folder", "Create a new folder in the active pane", () => NewFolderButton_Click(this, new RoutedEventArgs())),
            new("Toggle Preview Pane", "Show or hide the preview rail", () => TogglePreview()),
            new("Toggle Terminal", "Show or hide the terminal drawer", () => ToggleTerminal()),
            new("Undo", "Undo the last file operation", () => _ = UndoAndRefreshAsync()),
            new("Go Up", "Navigate to the parent folder", () => pane?.NavigateUp()),
            new("Go Back", "Navigate back", () => pane?.NavigateBack()),
            new("Go Forward", "Navigate forward", () => pane?.NavigateForward()),
            new("Refresh", "Reload the active pane's folder", () => pane?.Refresh()),
        };

        return commands;
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
