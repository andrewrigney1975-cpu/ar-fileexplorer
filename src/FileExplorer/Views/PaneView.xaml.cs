using System.Diagnostics;
using FileExplorer.Models;
using FileExplorer.Services;
using FileExplorer.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace FileExplorer.Views;

public sealed partial class PaneView : UserControl
{
    private const string InternalDragFormat = "FileExplorer.InternalDrag";

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(PaneViewModel), typeof(PaneView), new PropertyMetadata(null, OnViewModelChanged));

    public event EventHandler? Activated;

    public PaneView()
    {
        InitializeComponent();
    }

    public PaneViewModel? ViewModel
    {
        get => (PaneViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var pane = (PaneView)d;
        pane.DataContext = e.NewValue;

        if (e.OldValue is PaneViewModel oldVm)
        {
            oldVm.PropertyChanged -= pane.ViewModel_PropertyChanged;
        }

        if (e.NewValue is PaneViewModel newVm)
        {
            newVm.PropertyChanged += pane.ViewModel_PropertyChanged;
            pane.ApplyViewMode(newVm.ViewMode);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PaneViewModel.ViewMode) && ViewModel is not null)
        {
            ApplyViewMode(ViewModel.ViewMode);
        }
    }

    private void ApplyViewMode(ViewMode mode)
    {
        switch (mode)
        {
            case ViewMode.Icons:
                ItemsList.ItemTemplate = (DataTemplate)Resources["IconsTemplate"];
                ItemsList.ItemsPanel = (ItemsPanelTemplate)Resources["WrapPanelTemplate"];
                DetailsHeader.Visibility = Visibility.Collapsed;
                break;
            case ViewMode.List:
                ItemsList.ItemTemplate = (DataTemplate)Resources["ListTemplate"];
                ItemsList.ItemsPanel = (ItemsPanelTemplate)Resources["StackPanelTemplate"];
                DetailsHeader.Visibility = Visibility.Collapsed;
                break;
            case ViewMode.Details:
            default:
                ItemsList.ItemTemplate = (DataTemplate)Resources["DetailsTemplate"];
                ItemsList.ItemsPanel = (ItemsPanelTemplate)Resources["StackPanelTemplate"];
                DetailsHeader.Visibility = Visibility.Visible;
                break;
        }
    }

    private void RootGrid_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
    }

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.SelectedItems = ItemsList.SelectedItems.OfType<FileSystemItem>().ToList();
        }

        Activated?.Invoke(this, EventArgs.Empty);
    }

    private void PathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && ViewModel is not null)
        {
            var path = PathBox.Text.Trim();
            if (Directory.Exists(path))
            {
                ViewModel.NavigateTo(path);
            }
            else
            {
                PathBox.Text = ViewModel.CurrentPath;
            }
        }
    }

    private void ItemsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel?.SelectedItem is { } item)
        {
            OpenItem(item);
        }
    }

    private void OpenItem(FileSystemItem item)
    {
        if (item.IsDirectory)
        {
            ViewModel?.NavigateTo(item.FullPath);
        }
        else
        {
            try
            {
                Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // No associated application; ignore.
            }
        }
    }

    private FileSystemItem? _renamingItem;

    private async void ItemsList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (e.Key == VirtualKey.F2)
        {
            if (ItemsList.SelectedItems.Count != 1 || ViewModel.SelectedItem is not { } item)
            {
                return;
            }

            e.Handled = true;
            BeginRename(item);
        }
        else if (e.Key == VirtualKey.F3)
        {
            if (ItemsList.SelectedItems.Count == 0)
            {
                return;
            }

            e.Handled = true;
            await MoveSelectionToNewFolderAsync();
        }
        else if (e.Key == VirtualKey.Delete)
        {
            var items = ItemsList.SelectedItems.OfType<FileSystemItem>().ToList();
            if (items.Count == 0)
            {
                return;
            }

            e.Handled = true;
            await DeleteItemsAsync(items, permanent: IsShiftPressed());
        }
    }

    public async Task DeleteItemsAsync(IReadOnlyList<FileSystemItem> items, bool permanent)
    {
        if (items.Count == 0 || ViewModel is null)
        {
            return;
        }

        if (permanent)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Delete permanently?",
                Content = $"{items.Count} item{(items.Count == 1 ? "" : "s")} will be deleted permanently. This can't be undone.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        var paths = items.Select(i => i.FullPath).ToList();

        await Task.Run(() =>
        {
            foreach (var path in paths)
            {
                try
                {
                    DeleteOne(path, permanent);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // best-effort: skip items that fail (locked, permissions) and continue with the rest
                }
            }
        });

        ViewModel.Refresh();
    }

    private static void DeleteOne(string path, bool permanent)
    {
        var isDirectory = Directory.Exists(path);

        if (permanent)
        {
            if (isDirectory)
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        if (isDirectory)
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        else if (File.Exists(path))
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
    }

    private static bool IsShiftPressed()
    {
        var state = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        return (state & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }

    private async Task MoveSelectionToNewFolderAsync()
    {
        if (ViewModel is null)
        {
            return;
        }

        var items = ItemsList.SelectedItems.OfType<FileSystemItem>().ToList();
        if (items.Count == 0)
        {
            return;
        }

        const string defaultName = "New folder";
        var nameBox = new TextBox { Text = defaultName, SelectionStart = 0, SelectionLength = defaultName.Length };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Move to folder",
            PrimaryButtonText = "Move",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Move {items.Count} item{(items.Count == 1 ? "" : "s")} into a new folder in {ViewModel.CurrentPath}:",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    nameBox,
                },
            },
        };

        dialog.Opened += (_, _) => nameBox.Focus(FocusState.Programmatic);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var name = nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return;
        }

        var destination = FileOperationService.MakeUniqueDestination(Path.Combine(ViewModel.CurrentPath, name));

        try
        {
            Directory.CreateDirectory(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var sourcePaths = items.Select(i => i.FullPath).ToList();
        FileOperationQueueService.Current?.Enqueue(sourcePaths, destination, FileDropOperation.Move, destinationWasCreatedForThisJob: true);
    }

    private void BeginRename(FileSystemItem item)
    {
        if (ItemsList.ContainerFromItem(item) is not FrameworkElement container)
        {
            return;
        }

        var point = container.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));

        RenamePopup.XamlRoot = XamlRoot;
        RenamePopup.HorizontalOffset = point.X + 8;
        RenamePopup.VerticalOffset = point.Y + Math.Max(0, (container.ActualHeight - 32) / 2);

        _renamingItem = item;
        RenameTextBox.Text = item.Name;
        RenamePopup.IsOpen = true;

        RenameTextBox.Focus(FocusState.Programmatic);
        RenameTextBox.SelectionStart = 0;
        if (!item.IsDirectory)
        {
            var dot = item.Name.LastIndexOf('.');
            RenameTextBox.SelectionLength = dot > 0 ? dot : item.Name.Length;
        }
        else
        {
            RenameTextBox.SelectionLength = item.Name.Length;
        }
    }

    private void RenameTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            CommitRename();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            RenamePopup.IsOpen = false;
            _renamingItem = null;
        }
    }

    private void RenameTextBox_LostFocus(object sender, RoutedEventArgs e) => CommitRename();

    private void CommitRename()
    {
        if (!RenamePopup.IsOpen)
        {
            return;
        }

        RenamePopup.IsOpen = false;

        var item = _renamingItem;
        _renamingItem = null;
        if (item is null || ViewModel is null)
        {
            return;
        }

        var newName = RenameTextBox.Text.Trim();
        if (string.IsNullOrEmpty(newName) || newName == item.Name || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return;
        }

        var directory = Path.GetDirectoryName(item.FullPath)!;
        var newPath = Path.Combine(directory, newName);
        if (File.Exists(newPath) || Directory.Exists(newPath))
        {
            return;
        }

        try
        {
            if (item.IsDirectory)
            {
                Directory.Move(item.FullPath, newPath);
            }
            else
            {
                File.Move(item.FullPath, newPath);
            }

            UndoService.Instance.Push(new RenameUndo(item.FullPath, newPath));
            ViewModel.Refresh(newPath);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void ItemsList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var paths = e.Items.OfType<FileSystemItem>().Select(i => i.FullPath).ToList();
        if (paths.Count == 0)
        {
            return;
        }

        e.Data.SetText(string.Join('\n', paths));
        e.Data.Properties.Add(InternalDragFormat, true);
        e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
    }

    private async void ItemsList_DragOver(object sender, DragEventArgs e)
    {
        if (ViewModel is null || !e.DataView.Properties.ContainsKey(InternalDragFormat))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var text = await e.DataView.GetTextAsync();
            var sourcePaths = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (sourcePaths.Length == 0)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            var container = FindDirectoryContainerUnderPoint(e.GetPosition(ItemsList));
            SetDropHighlight(container);
            var targetFolder = container is { Content: FileSystemItem dir } ? dir.FullPath : ViewModel.CurrentPath;

            if (!FileOperationService.IsValidDropTarget(sourcePaths, targetFolder))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            bool forceMove = IsAltPressed();
            bool sameDrive = FileOperationService.SameDrive(sourcePaths[0], targetFolder);
            var op = forceMove || sameDrive ? FileDropOperation.Move : FileDropOperation.Copy;

            e.AcceptedOperation = op == FileDropOperation.Move ? DataPackageOperation.Move : DataPackageOperation.Copy;
            e.DragUIOverride.IsCaptionVisible = true;
            var targetName = Path.GetFileName(targetFolder.TrimEnd('\\'));
            e.DragUIOverride.Caption = (op == FileDropOperation.Move ? "Move to " : "Copy to ") + targetName;
            e.DragUIOverride.IsGlyphVisible = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void ItemsList_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel is null || !e.DataView.Properties.ContainsKey(InternalDragFormat))
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var text = await e.DataView.GetTextAsync();
            var sourcePaths = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (sourcePaths.Length == 0)
            {
                return;
            }

            var container = FindDirectoryContainerUnderPoint(e.GetPosition(ItemsList));
            var targetFolder = container is { Content: FileSystemItem dir } ? dir.FullPath : ViewModel.CurrentPath;

            if (!FileOperationService.IsValidDropTarget(sourcePaths, targetFolder))
            {
                return;
            }

            bool forceMove = IsAltPressed();
            bool sameDrive = FileOperationService.SameDrive(sourcePaths[0], targetFolder);
            var op = forceMove || sameDrive ? FileDropOperation.Move : FileDropOperation.Copy;

            FileOperationQueueService.Current?.Enqueue(sourcePaths, targetFolder, op);
        }
        finally
        {
            SetDropHighlight(null);
            deferral.Complete();
        }
    }

    private void ItemsList_DragLeave(object sender, DragEventArgs e) => SetDropHighlight(null);

    /// The directory row's container under this point, or null when hovering a file or empty space.
    private ListViewItem? FindDirectoryContainerUnderPoint(Windows.Foundation.Point pointOnItemsList)
    {
        foreach (var element in VisualTreeHelper.FindElementsInHostCoordinates(pointOnItemsList, ItemsList))
        {
            if (element is ListViewItem { Content: FileSystemItem { IsDirectory: true } } container)
            {
                return container;
            }
        }

        return null;
    }

    private ListViewItem? _dropHighlightContainer;

    private void SetDropHighlight(ListViewItem? container)
    {
        if (ReferenceEquals(_dropHighlightContainer, container))
        {
            return;
        }

        if (_dropHighlightContainer is not null)
        {
            _dropHighlightContainer.Background = null;
        }

        if (container is not null)
        {
            container.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(70, 0, 120, 215));
        }

        _dropHighlightContainer = container;
    }

    private FileSystemItem? FindItemUnderPoint(Windows.Foundation.Point pointOnItemsList)
    {
        foreach (var element in VisualTreeHelper.FindElementsInHostCoordinates(pointOnItemsList, ItemsList))
        {
            if (element is ListViewItem { Content: FileSystemItem item })
            {
                return item;
            }
        }

        return null;
    }

    private void ItemsList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var point = e.GetPosition(ItemsList);
        var tapped = FindItemUnderPoint(point);

        if (tapped is not null && !ItemsList.SelectedItems.Contains(tapped))
        {
            ItemsList.SelectedItem = tapped;
        }

        var selection = ItemsList.SelectedItems.OfType<FileSystemItem>().ToList();
        var menu = tapped is not null ? BuildItemContextMenu(selection) : BuildEmptySpaceContextMenu();

        menu.ShowAt(ItemsList, new FlyoutShowOptions { Position = point });
        e.Handled = true;
    }

    private MenuFlyout BuildItemContextMenu(IReadOnlyList<FileSystemItem> selection)
    {
        var menu = new MenuFlyout();

        if (selection.Count == 1)
        {
            var single = selection[0];
            menu.Items.Add(NewMenuItem("Open", "", () => OpenItem(single)));
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        menu.Items.Add(NewMenuItem("Cut", "", () => SetClipboardFromSelection(selection, isCut: true)));
        menu.Items.Add(NewMenuItem("Copy", "", () => SetClipboardFromSelection(selection, isCut: false)));

        if (selection.Count == 1)
        {
            menu.Items.Add(NewMenuItem("Rename", "", () => BeginRename(selection[0])));
        }

        menu.Items.Add(NewMenuItem("Move to folder...", "", async () => await MoveSelectionToNewFolderAsync()));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(NewMenuItem("Delete", "", async () => await DeleteItemsAsync(selection, permanent: false)));

        return menu;
    }

    private MenuFlyout BuildEmptySpaceContextMenu()
    {
        var menu = new MenuFlyout();
        var paste = NewMenuItem("Paste", "", () => FileClipboardService.Instance.PasteInto(ViewModel!.CurrentPath));
        paste.IsEnabled = FileClipboardService.Instance.HasContent;
        menu.Items.Add(paste);
        menu.Items.Add(NewMenuItem("New folder", "", CreateNewFolderHere));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(NewMenuItem("Refresh", "", () => ViewModel?.Refresh()));
        return menu;
    }

    private void CreateNewFolderHere()
    {
        if (ViewModel is null)
        {
            return;
        }

        var basePath = ViewModel.CurrentPath;
        var candidate = Path.Combine(basePath, "New folder");

        for (int i = 2; Directory.Exists(candidate) || File.Exists(candidate); i++)
        {
            candidate = Path.Combine(basePath, $"New folder ({i})");
        }

        try
        {
            Directory.CreateDirectory(candidate);
            UndoService.Instance.Push(new CreateFolderUndo(candidate));
            ViewModel.Refresh(candidate);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void SetClipboardFromSelection(IReadOnlyList<FileSystemItem> selection, bool isCut)
    {
        if (selection.Count == 0)
        {
            return;
        }

        FileClipboardService.Instance.Set(selection.Select(i => i.FullPath).ToList(), isCut);
    }

    private static MenuFlyoutItem NewMenuItem(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }

    private static bool IsAltPressed()
    {
        var state = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu);
        return (state & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }
}
