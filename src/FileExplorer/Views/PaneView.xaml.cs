using System.Diagnostics;
using System.IO.Compression;
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

        // handledEventsToo: ListView's inner ScrollViewer consumes Space for page-down before it
        // would otherwise bubble here, which would silently eat the quick-look shortcut.
        ItemsList.AddHandler(KeyDownEvent, new KeyEventHandler(ItemsList_KeyDown), true);
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
            pane.UpdateBreadcrumb(newVm.CurrentPath);
            pane.UpdateSortIndicators();
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PaneViewModel.ViewMode) && ViewModel is not null)
        {
            ApplyViewMode(ViewModel.ViewMode);
        }
        else if (e.PropertyName == nameof(PaneViewModel.CurrentPath) && ViewModel is not null)
        {
            UpdateBreadcrumb(ViewModel.CurrentPath);
            ShowBreadcrumb();
        }
        else if (e.PropertyName is nameof(PaneViewModel.ActiveSortColumn) or nameof(PaneViewModel.SortAscending))
        {
            UpdateSortIndicators();
        }
    }

    private void NameHeader_Tapped(object sender, TappedRoutedEventArgs e) => ViewModel?.ToggleSort(SortColumn.Name);

    private void ModifiedHeader_Tapped(object sender, TappedRoutedEventArgs e) => ViewModel?.ToggleSort(SortColumn.Modified);

    private void KindHeader_Tapped(object sender, TappedRoutedEventArgs e) => ViewModel?.ToggleSort(SortColumn.Kind);

    private void SizeHeader_Tapped(object sender, TappedRoutedEventArgs e) => ViewModel?.ToggleSort(SortColumn.Size);

    private void UpdateSortIndicators()
    {
        var icons = new[] { NameSortIcon, ModifiedSortIcon, KindSortIcon, SizeSortIcon };
        foreach (var icon in icons)
        {
            icon.Visibility = Visibility.Collapsed;
        }

        if (ViewModel?.ActiveSortColumn is not { } column)
        {
            return;
        }

        var activeIcon = column switch
        {
            SortColumn.Name => NameSortIcon,
            SortColumn.Modified => ModifiedSortIcon,
            SortColumn.Kind => KindSortIcon,
            SortColumn.Size => SizeSortIcon,
            _ => NameSortIcon,
        };

        activeIcon.Glyph = ViewModel.SortAscending ? "" : "";
        activeIcon.Visibility = Visibility.Visible;
    }

    private void UpdateBreadcrumb(string path)
    {
        BreadcrumbPanel.Children.Clear();

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        var segments = new List<(string Label, string FullPath)> { (root.TrimEnd('\\'), root) };
        var accumulated = root;
        foreach (var part in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            accumulated = Path.Combine(accumulated, part);
            segments.Add((part, accumulated));
        }

        for (int i = 0; i < segments.Count; i++)
        {
            var (label, fullPath) = segments[i];
            var button = new Button { Content = label, Style = (Style)Application.Current.Resources["BreadcrumbButtonStyle"] };
            button.Click += (_, _) => ViewModel?.NavigateTo(fullPath);
            BreadcrumbPanel.Children.Add(button);

            if (i < segments.Count - 1)
            {
                BreadcrumbPanel.Children.Add(new FontIcon
                {
                    Glyph = "",
                    FontSize = 10,
                    Opacity = 0.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 0),
                });
            }
        }
    }

    private void BreadcrumbHost_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        BreadcrumbHost.Visibility = Visibility.Collapsed;
        PathBox.Visibility = Visibility.Visible;
        PathBox.Text = ViewModel?.CurrentPath ?? string.Empty;
        PathBox.Focus(FocusState.Programmatic);
        PathBox.SelectAll();
    }

    private void ShowBreadcrumb()
    {
        PathBox.Visibility = Visibility.Collapsed;
        BreadcrumbHost.Visibility = Visibility.Visible;
    }

    private void PathBox_LostFocus(object sender, RoutedEventArgs e) => ShowBreadcrumb();

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && ViewModel is not null)
        {
            ViewModel.SearchText = string.Empty;
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
            case ViewMode.Gallery:
                ItemsList.ItemTemplate = (DataTemplate)Resources["GalleryTemplate"];
                ItemsList.ItemsPanel = (ItemsPanelTemplate)Resources["WrapPanelTemplate"];
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

    private async void ThumbnailHost_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileSystemItem item })
        {
            await item.EnsureThumbnailAsync();
        }
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
                ShowBreadcrumb();
            }
            else
            {
                PathBox.Text = ViewModel.CurrentPath;
            }
        }
        else if (e.Key == VirtualKey.Escape)
        {
            ShowBreadcrumb();
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

    private static void OpenWithPicker(FileSystemItem item)
    {
        if (item.IsDirectory)
        {
            return;
        }

        try
        {
            // Invokes the native "Open with" dialog; no COM interop needed.
            Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL \"{item.FullPath}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
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
            var selected = ItemsList.SelectedItems.OfType<FileSystemItem>().ToList();
            if (selected.Count == 1)
            {
                e.Handled = true;
                BeginRename(selected[0]);
            }
            else if (selected.Count > 1)
            {
                e.Handled = true;
                await BatchRenameAsync(selected);
            }
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
        else if (e.Key == VirtualKey.Space)
        {
            if (ViewModel.SelectedItem is null)
            {
                return;
            }

            e.Handled = true;
            ToggleQuickLook();
        }
    }

    private void ToggleQuickLook()
    {
        if (QuickLookPopup.IsOpen)
        {
            QuickLookPopup.IsOpen = false;
            return;
        }

        QuickLookPreview.ViewModel = ViewModel;
        QuickLookPopup.XamlRoot = XamlRoot;

        var center = RootGrid.TransformToVisual(null).TransformPoint(
            new Windows.Foundation.Point(RootGrid.ActualWidth / 2, RootGrid.ActualHeight / 2));
        QuickLookPopup.HorizontalOffset = center.X - 220;
        QuickLookPopup.VerticalOffset = center.Y - 280;

        QuickLookPopup.IsOpen = true;
    }

    private void QuickLookCloseButton_Click(object sender, RoutedEventArgs e) => QuickLookPopup.IsOpen = false;

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
        var failures = new List<(string Path, string Error)>();

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
                    // Skip items that fail (locked, permissions, non-empty edge cases) and continue
                    // with the rest, but the failure must be visible - silently doing nothing here
                    // makes a real error indistinguishable from "it just didn't work".
                    failures.Add((path, ex.Message));
                }
            }
        });

        ViewModel.Refresh();

        if (failures.Count > 0)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = failures.Count == 1 ? "Couldn't delete item" : $"Couldn't delete {failures.Count} items",
                Content = new TextBlock
                {
                    Text = string.Join("\n\n", failures.Select(f => FormatDeleteFailure(f.Path, f.Error))),
                    TextWrapping = TextWrapping.Wrap,
                },
                CloseButtonText = "Close",
            };

            await dialog.ShowAsync();
        }
    }

    /// "Access is denied" on a non-empty folder is almost always either a locked/open file inside
    /// it or (very commonly, when the path is under OneDrive/etc.) an online-only cloud placeholder
    /// that hasn't finished downloading - the raw exception text alone doesn't hint at either.
    private static string FormatDeleteFailure(string path, string error)
    {
        var isAccessDenied = error.Contains("denied", StringComparison.OrdinalIgnoreCase);
        var hint = isAccessDenied
            ? CloudProviderService.IsUnderCloudRoot(path)
                ? " This folder is inside a cloud-sync location - a file inside it may still be online-only and not fully downloaded yet. Try opening the folder and waiting for it to finish syncing, then delete again."
                : " A file inside this folder may be open in another program, or read-only/protected."
            : string.Empty;

        return $"{Path.GetFileName(path)}:\n{error}{hint}";
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

    private async Task SetSyncTargetAsync(string targetPath)
    {
        var source = SyncTaskService.PendingSourcePath;
        if (source is null)
        {
            return;
        }

        SyncTaskService.SetPendingTarget(targetPath);

        var nameBox = new TextBox
        {
            PlaceholderText = "Sync task name",
            Text = $"{Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar))} -> {Path.GetFileName(targetPath.TrimEnd(Path.DirectorySeparatorChar))}",
        };

        var dialog = new ContentDialog
        {
            Title = "Name This Sync Task",
            Content = nameBox,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(nameBox.Text))
        {
            SyncTaskService.ClearPending();
            return;
        }

        SyncTaskService.AddTask(nameBox.Text.Trim(), source, targetPath);
        SyncTaskService.ClearPending();
    }

    private async Task ComputeHashesAsync(IReadOnlyList<FileSystemItem> selection)
    {
        if (selection.Count == 0)
        {
            return;
        }

        var resultBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Height = 220,
            Text = "Computing...",
        };

        var copyButton = new Button { Content = "Copy to clipboard", Margin = new Thickness(0, 8, 0, 0) };
        copyButton.Click += (_, _) =>
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(resultBox.Text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "SHA-256 checksum",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            Content = new StackPanel { Spacing = 4, Children = { resultBox, copyButton } },
        };

        var showTask = dialog.ShowAsync().AsTask();

        var lines = new List<string>();
        foreach (var item in selection)
        {
            if (item.IsDirectory)
            {
                lines.Add($"{item.Name}: (folder - skipped)");
                continue;
            }

            try
            {
                await using var stream = File.OpenRead(item.FullPath);
                var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream);
                lines.Add($"{item.Name}:\n{Convert.ToHexString(hash)}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lines.Add($"{item.Name}: (could not read file)");
            }

            resultBox.Text = string.Join("\n\n", lines);
        }

        await showTask;
    }

    private async Task CompressSelectionAsync(IReadOnlyList<FileSystemItem> selection)
    {
        if (selection.Count == 0 || ViewModel is null)
        {
            return;
        }

        var baseName = selection.Count == 1 ? Path.GetFileNameWithoutExtension(selection[0].Name) : "Archive";
        var zipPath = FileOperationService.MakeUniqueDestination(Path.Combine(ViewModel.CurrentPath, baseName + ".zip"));

        await Task.Run(() =>
        {
            try
            {
                using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
                foreach (var item in selection)
                {
                    if (item.IsDirectory)
                    {
                        AddDirectoryToZip(archive, item.FullPath, item.Name);
                    }
                    else
                    {
                        archive.CreateEntryFromFile(item.FullPath, item.Name, CompressionLevel.Optimal);
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        });

        UndoService.Instance.Push(new CopyUndo(new List<string> { zipPath }));
        ViewModel.Refresh(zipPath);
    }

    private static void AddDirectoryToZip(ZipArchive archive, string sourceDir, string entryPrefix)
    {
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
            archive.CreateEntryFromFile(file, $"{entryPrefix}/{relative}", CompressionLevel.Optimal);
        }
    }

    private async Task ExtractZipsAsync(IReadOnlyList<FileSystemItem> items)
    {
        if (ViewModel is null || items.Count == 0)
        {
            return;
        }

        var destinations = new List<string>();

        foreach (var item in items)
        {
            var destination = FileOperationService.MakeUniqueDestination(
                Path.Combine(ViewModel.CurrentPath, Path.GetFileNameWithoutExtension(item.Name)));

            try
            {
                await Task.Run(() => ZipFile.ExtractToDirectory(item.FullPath, destination));
                destinations.Add(destination);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
            }
        }

        if (destinations.Count == 0)
        {
            return;
        }

        UndoService.Instance.Push(new CopyUndo(destinations));
        ViewModel.Refresh(destinations[^1]);
    }

    private async Task BatchRenameAsync(IReadOnlyList<FileSystemItem> selection)
    {
        if (selection.Count < 2 || ViewModel is null)
        {
            return;
        }

        var patternBox = new TextBox { Text = "{name}", PlaceholderText = "e.g. Vacation {n:000}" };
        var previewText = new TextBlock { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };

        void UpdatePreview()
        {
            var lines = selection.Take(4).Select((item, i) => $"{item.Name}  ->  {ApplyPattern(patternBox.Text, item, i)}{item.Extension}");
            var extra = selection.Count > 4 ? $"\n... and {selection.Count - 4} more" : "";
            previewText.Text = string.Join("\n", lines) + extra;
        }

        patternBox.TextChanged += (_, _) => UpdatePreview();
        UpdatePreview();

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Rename {selection.Count} items",
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = "{name} is the original name; {n} or {n:000} is a sequence number.",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        Opacity = 0.8,
                    },
                    patternBox,
                    previewText,
                },
            },
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var pattern = patternBox.Text;
        var renamed = new List<(string Source, string Destination)>();

        for (int i = 0; i < selection.Count; i++)
        {
            var item = selection[i];
            var newName = ApplyPattern(pattern, item, i) + item.Extension;
            var newPath = Path.Combine(Path.GetDirectoryName(item.FullPath)!, newName);

            if (string.Equals(newPath, item.FullPath, StringComparison.OrdinalIgnoreCase) ||
                File.Exists(newPath) || Directory.Exists(newPath))
            {
                continue;
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

                renamed.Add((item.FullPath, newPath));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        if (renamed.Count > 0)
        {
            UndoService.Instance.Push(new MoveUndo(renamed));
        }

        ViewModel.Refresh();
    }

    private static string ApplyPattern(string pattern, FileSystemItem item, int index)
    {
        var nameNoExt = item.IsDirectory ? item.Name : Path.GetFileNameWithoutExtension(item.Name);
        var result = pattern.Replace("{name}", nameNoExt);

        return System.Text.RegularExpressions.Regex.Replace(result, @"\{n(:([0#]+))?\}", m =>
        {
            var number = index + 1;
            return m.Groups[2].Success ? number.ToString(m.Groups[2].Value) : number.ToString();
        });
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

    /// The realized ListViewItem container whose bounds contain this ItemsList-relative point, or
    /// null if none (empty space, or a virtualized-out container). Avoids
    /// VisualTreeHelper.FindElementsInHostCoordinates, which needs coordinates in the app's root
    /// space (not ItemsList-relative) and was silently returning zero hits here.
    private ListViewItem? FindContainerUnderPoint(Windows.Foundation.Point pointOnItemsList)
    {
        foreach (var item in ItemsList.Items)
        {
            if (ItemsList.ContainerFromItem(item) is not ListViewItem container)
            {
                continue;
            }

            var bounds = container.TransformToVisual(ItemsList)
                .TransformBounds(new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight));

            if (bounds.Contains(pointOnItemsList))
            {
                return container;
            }
        }

        return null;
    }

    /// The directory row's container under this point, or null when hovering a file or empty space.
    private ListViewItem? FindDirectoryContainerUnderPoint(Windows.Foundation.Point pointOnItemsList)
    {
        var container = FindContainerUnderPoint(pointOnItemsList);
        return container?.Content is FileSystemItem { IsDirectory: true } ? container : null;
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
        return FindContainerUnderPoint(pointOnItemsList)?.Content as FileSystemItem;
    }

    private void ItemsList_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (!e.TryGetPosition(ItemsList, out var point))
        {
            point = new Windows.Foundation.Point(0, 0);
        }

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
            if (!single.IsDirectory)
            {
                menu.Items.Add(NewMenuItem("Open with...", "", () => OpenWithPicker(single)));
            }
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        menu.Items.Add(NewMenuItem("Cut", "", () => SetClipboardFromSelection(selection, isCut: true)));
        menu.Items.Add(NewMenuItem("Copy", "", () => SetClipboardFromSelection(selection, isCut: false)));

        if (selection.Count == 1)
        {
            menu.Items.Add(NewMenuItem("Rename", "", () => BeginRename(selection[0])));
        }
        else if (selection.Count > 1)
        {
            menu.Items.Add(NewMenuItem("Rename...", "", async () => await BatchRenameAsync(selection)));
        }

        menu.Items.Add(NewMenuItem("Move to folder...", "", async () => await MoveSelectionToNewFolderAsync()));
        menu.Items.Add(NewMenuItem("Compress to .zip", "", async () => await CompressSelectionAsync(selection)));
        if (selection.Count > 0 && selection.All(item => string.Equals(item.Extension, ".zip", StringComparison.OrdinalIgnoreCase)))
        {
            menu.Items.Add(NewMenuItem("Extract", "", async () => await ExtractZipsAsync(selection)));
        }
        if (selection.Count == 1 && selection[0].IsDirectory)
        {
            var folder = selection[0];
            menu.Items.Add(NewMenuItem("Set sync source...", "", () => SyncTaskService.SetPendingSource(folder.FullPath)));

            if (SyncTaskService.PendingSourcePath is { } pendingSource &&
                !string.Equals(pendingSource, folder.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                menu.Items.Add(NewMenuItem("Set sync target", "", async () => await SetSyncTargetAsync(folder.FullPath)));
            }
        }

        menu.Items.Add(NewMenuItem("Compute hash...", "", async () => await ComputeHashesAsync(selection)));
        menu.Items.Add(BuildTagSubMenu(selection));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(NewMenuItem("Delete", "", async () => await DeleteItemsAsync(selection, permanent: false)));

        return menu;
    }

    private MenuFlyoutSubItem BuildTagSubMenu(IReadOnlyList<FileSystemItem> selection)
    {
        var subMenu = new MenuFlyoutSubItem { Text = "Tag" };

        foreach (var colorName in TagService.ColorNames)
        {
            var swatch = new MenuFlyoutItem
            {
                Text = colorName,
                Icon = new FontIcon
                {
                    Glyph = "",
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)new Converters.TagColorToBrushConverter()
                        .Convert(colorName, typeof(Microsoft.UI.Xaml.Media.Brush), null!, string.Empty),
                },
            };
            swatch.Click += (_, _) => SetTagForSelection(selection, colorName);
            subMenu.Items.Add(swatch);
        }

        subMenu.Items.Add(new MenuFlyoutSeparator());
        var clear = new MenuFlyoutItem { Text = "Remove Tag" };
        clear.Click += (_, _) => SetTagForSelection(selection, null);
        subMenu.Items.Add(clear);

        return subMenu;
    }

    private void SetTagForSelection(IReadOnlyList<FileSystemItem> selection, string? colorName)
    {
        foreach (var item in selection)
        {
            TagService.SetColor(item.FullPath, colorName);
        }

        ViewModel?.Refresh();
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
