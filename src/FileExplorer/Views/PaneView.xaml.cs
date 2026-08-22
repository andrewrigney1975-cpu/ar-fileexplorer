using System.Diagnostics;
using FileExplorer.Models;
using FileExplorer.Services;
using FileExplorer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
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

        List<(string Label, string FullPath)> segments;

        if (RemotePathService.TryParse(path, out var scheme, out var connectionId, out _))
        {
            var rootLabel = App.Services.GetRequiredService<IRemoteConnectionService>().Find(connectionId)?.Name ?? connectionId;
            segments = new List<(string Label, string FullPath)> { (rootLabel, RemotePathService.BuildRoot(scheme, connectionId)) };
            segments.AddRange(RemotePathService.GetBreadcrumbSegments(path).Select(s => (s.Name, s.Path)));
        }
        else
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            segments = new List<(string Label, string FullPath)> { (root.TrimEnd('\\'), root) };
            var accumulated = root;
            foreach (var part in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                accumulated = Path.Combine(accumulated, part);
                segments.Add((part, accumulated));
            }
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

    // ListView recycles containers as you scroll: the same Grid gets rebound to new data instead of
    // being freshly added to the tree, so its Loaded event (ThumbnailHost_Loaded, above) only ever
    // fires once per container - never again for whichever items later get virtualized into it. This
    // is the reliable per-recycle hook that actually covers every item, not just the first screenful.
    private void ItemsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is FileSystemItem item)
        {
            _ = item.EnsureThumbnailAsync();
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
            if (RemotePathService.IsRemote(path) || Directory.Exists(path))
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
        catch (System.ComponentModel.Win32Exception ex)
        {
            LoggingService.LogWarning("PaneView.OpenWithPicker", ex);
        }
    }

    private FileSystemItem? _renamingItem;

    private void ItemsList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Space is the standard activation key for any focused button-like control throughout
        // WinUI (Button, ToggleButton, CheckBox, MenuFlyoutItem...) - unlike Delete/F2/F3, it can't
        // safely become a window-global KeyboardAccelerator without hijacking Space-to-activate
        // everywhere else in the app. QuickLook staying focus-scoped to the list is also the
        // semantically correct behavior here, not just a workaround.
        if (ViewModel is null || e.Key != VirtualKey.Space || ViewModel.SelectedItem is null)
        {
            return;
        }

        e.Handled = true;
        ToggleQuickLook();
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
        if (ViewModel is null)
        {
            return;
        }

        await DeleteService.DeleteItemsAsync(items, permanent, XamlRoot, () => ViewModel.Refresh());
    }

    private async Task ShowPropertiesAsync(IReadOnlyList<FileSystemItem> selection)
    {
        if (selection.Count == 0)
        {
            return;
        }

        var content = new PropertiesDialog(selection);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = selection.Count == 1 ? $"{selection[0].Name} Properties" : $"Properties - {selection.Count} items",
            Content = content,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Closed += (_, _) => content.CancelSizeComputation();

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            content.ApplyAttributeChanges();
        }
    }

    private static void ToggleFavourite(string path)
    {
        if (FavouriteService.IsFavourite(path))
        {
            var existing = FavouriteService.Load().FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                FavouriteService.Remove(existing);
            }
            return;
        }

        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        FavouriteService.Add(new FavouriteLocation(string.IsNullOrEmpty(name) ? path : name, path));
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

        var includeHiddenSystemBox = new CheckBox
        {
            Content = "Include hidden/system files",
            IsChecked = false,
        };

        var dialog = new ContentDialog
        {
            Title = "Name This Sync Task",
            Content = new StackPanel { Spacing = 8, Children = { nameBox, includeHiddenSystemBox } },
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

        SyncTaskService.AddTask(nameBox.Text.Trim(), source, targetPath, includeHiddenSystemBox.IsChecked == true);
        SyncTaskService.ClearPending();
    }

    private async Task WatchFolderAsync(string folderPath)
    {
        var scripts = ScriptService.List();
        if (scripts.Count == 0)
        {
            var noScriptsDialog = new ContentDialog
            {
                Title = "No Scripts Available",
                Content = "Create a script first (Command Palette > Manage Scripts...) before watching a folder.",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
            };
            await noScriptsDialog.ShowAsync();
            return;
        }

        var scriptPicker = new ComboBox
        {
            ItemsSource = scripts,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var dialog = new ContentDialog
        {
            Title = "Watch This Folder",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = $"Run a script whenever files are added to \"{folderPath}\":", TextWrapping = TextWrapping.Wrap },
                    scriptPicker,
                },
            },
            PrimaryButtonText = "Watch",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || scriptPicker.SelectedItem is not string scriptName)
        {
            return;
        }

        WatchService.AddTask(folderPath, scriptName);
    }

    /// F8 behavior. Called from MainWindow's global F8 KeyboardAccelerator (driven by the active
    /// pane, not literal focus - see the Delete/F2/F3 accelerators for why).
    public async Task ComputeHashesForSelectionAsync()
    {
        var selected = ItemsList.SelectedItems.OfType<FileSystemItem>().ToList();
        if (selected.Count > 0)
        {
            await ComputeHashesAsync(selected);
        }
    }

    private async Task ComputeHashesAsync(IReadOnlyList<FileSystemItem> selection)
    {
        if (selection.Count == 0)
        {
            return;
        }

        var algorithmBox = new ComboBox
        {
            ItemsSource = new[] { "SHA-256", "SHA-1", "MD5" },
            SelectedIndex = 0,
            Width = 140,
        };
        var expectedBox = new TextBox { PlaceholderText = "Expected hash (optional, to verify)" };

        var summaryText = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

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

        // (Name, Hex hash or null if unreadable/a folder) - cached per algorithm so typing into
        // the expected-hash box only re-renders the match markers, never re-reads/re-hashes files.
        var results = new List<(string Name, string? Hex)>();

        void Render()
        {
            var expected = expectedBox.Text.Trim();
            var lines = new List<string>();

            foreach (var (name, hex) in results)
            {
                if (hex is null)
                {
                    lines.Add($"{name}: (folder or unreadable - skipped)");
                    continue;
                }

                var matchSuffix = string.IsNullOrEmpty(expected)
                    ? string.Empty
                    : string.Equals(hex, expected, StringComparison.OrdinalIgnoreCase) ? "  [MATCH]" : "  [NO MATCH]";
                lines.Add($"{name}:{matchSuffix}\n{hex}");
            }

            resultBox.Text = string.Join("\n\n", lines);

            var readable = results.Where(r => r.Hex is not null).ToList();
            if (string.IsNullOrEmpty(expected) && readable.Count == 2)
            {
                var identical = string.Equals(readable[0].Hex, readable[1].Hex, StringComparison.OrdinalIgnoreCase);
                summaryText.Text = identical ? "The two files are identical" : "The two files differ";
                summaryText.Foreground = new SolidColorBrush(identical
                    ? Windows.UI.Color.FromArgb(255, 16, 137, 62)
                    : Windows.UI.Color.FromArgb(255, 232, 17, 35));
                summaryText.Visibility = Visibility.Visible;
            }
            else
            {
                summaryText.Visibility = Visibility.Collapsed;
            }
        }

        async Task<Stream> OpenReadStreamAsync(FileSystemItem item)
        {
            if (!item.IsRemote)
            {
                return File.OpenRead(item.FullPath);
            }

            if (!RemotePathService.TryParse(item.FullPath, out _, out var connectionId, out var remotePath))
            {
                throw new IOException("Invalid remote path.");
            }

            var session = RemoteSessionManager.TryGetSession(connectionId) ?? throw new IOException("Not connected.");
            return await session.OpenReadAsync(remotePath, CancellationToken.None);
        }

        async Task ComputeAsync()
        {
            results.Clear();
            resultBox.Text = "Computing...";
            var algorithm = (string)algorithmBox.SelectedItem;

            foreach (var item in selection)
            {
                if (item.IsDirectory)
                {
                    results.Add((item.Name, null));
                    Render();
                    continue;
                }

                try
                {
                    await using var stream = await OpenReadStreamAsync(item);
                    var hash = algorithm switch
                    {
                        "SHA-1" => await System.Security.Cryptography.SHA1.HashDataAsync(stream),
                        "MD5" => await System.Security.Cryptography.MD5.HashDataAsync(stream),
                        _ => await System.Security.Cryptography.SHA256.HashDataAsync(stream),
                    };
                    results.Add((item.Name, Convert.ToHexString(hash)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    results.Add((item.Name, null));
                }

                Render();
            }
        }

        algorithmBox.SelectionChanged += async (_, _) => await ComputeAsync();
        expectedBox.TextChanged += (_, _) => Render();

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Checksum",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            Content = new StackPanel
            {
                Spacing = 8,
                Width = 460,
                Children = { algorithmBox, expectedBox, summaryText, resultBox, copyButton },
            },
        };

        var showTask = dialog.ShowAsync().AsTask();
        await ComputeAsync();
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

        await Task.Run(() => ArchiveService.CreateZip(zipPath, selection));

        UndoService.Instance.Push(new CopyUndo(new List<string> { zipPath }));
        ViewModel.Refresh(zipPath);
    }

    /// Extracts .zip/.rar/.7z/.tar/.gz/.tgz/.bz2/.xz - SharpCompress auto-detects the actual format
    /// from content (so e.g. "backup.tgz" or "logs.tar.gz" work the same as a plain .tar).
    private async Task ExtractZipsAsync(IReadOnlyList<FileSystemItem> items)
    {
        if (ViewModel is null || items.Count == 0)
        {
            return;
        }

        var destinations = new List<string>();
        var failures = new List<(string Name, string Error)>();

        foreach (var item in items)
        {
            var destination = FileOperationService.MakeUniqueDestination(
                Path.Combine(ViewModel.CurrentPath, Path.GetFileNameWithoutExtension(item.Name)));

            try
            {
                await Task.Run(() => ArchiveService.Extract(item.FullPath, destination));
                destinations.Add(destination);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException
                or UnauthorizedAccessException or SharpCompress.Common.ExtractionException)
            {
                LoggingService.LogWarning($"PaneView.ExtractZipsAsync: {item.FullPath}", ex);
                failures.Add((item.Name, ex.Message));
            }
        }

        if (destinations.Count > 0)
        {
            UndoService.Instance.Push(new CopyUndo(destinations));
            ViewModel.Refresh(destinations[^1]);
        }

        if (failures.Count > 0)
        {
            var message = string.Join("\n", failures.Select(f => $"{f.Name}: {f.Error}"));
            await ShowErrorAsync(failures.Count == 1 ? "Couldn't extract archive" : "Some archives couldn't be extracted", message);
        }
    }

    /// F2 behavior: rename the single selected item in place, or open the batch-rename dialog for
    /// a multi-selection. Called from MainWindow's global F2 KeyboardAccelerator (driven by the
    /// active pane, not literal focus - see the Delete/Shift+Delete accelerators for why).
    public async Task RenameSelectionAsync()
    {
        var selected = ItemsList.SelectedItems.OfType<FileSystemItem>().ToList();
        if (selected.Count == 1)
        {
            BeginRename(selected[0]);
        }
        else if (selected.Count > 1)
        {
            await BatchRenameAsync(selected);
        }
    }

    private async Task BatchRenameAsync(IReadOnlyList<FileSystemItem> selection)
    {
        if (selection.Count < 2 || ViewModel is null)
        {
            return;
        }

        var patternRadio = new RadioButton { Content = "Pattern", GroupName = "BatchRenameMode", IsChecked = true };
        var regexRadio = new RadioButton { Content = "Find & Replace (Regex)", GroupName = "BatchRenameMode" };
        var guidRadio = new RadioButton { Content = "Random GUID", GroupName = "BatchRenameMode" };

        // Generated once so the preview and the rename that actually runs agree on the same names.
        var guidNames = selection.Select(_ => Guid.NewGuid().ToString("N")).ToList();
        var guidPanel = new StackPanel
        {
            Spacing = 8,
            Visibility = Visibility.Collapsed,
            Children =
            {
                new TextBlock
                {
                    Text = "Every selected item is renamed to a new random GUID, extension preserved.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Opacity = 0.8,
                },
            },
        };

        var patternBox = new TextBox { Text = "{name}", PlaceholderText = "e.g. Vacation {n:000}" };
        var patternPanel = new StackPanel
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
            },
        };

        var findBox = new TextBox { PlaceholderText = "Find (regex)" };
        var replaceBox = new TextBox { PlaceholderText = "Replace with (use $1, $2 for capture groups)" };
        var caseSensitiveBox = new CheckBox { Content = "Case sensitive" };
        var regexPanel = new StackPanel
        {
            Spacing = 8,
            Visibility = Visibility.Collapsed,
            Children =
            {
                new TextBlock
                {
                    Text = "Matches against the full file name, including its extension. .NET regex syntax.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Opacity = 0.8,
                },
                findBox,
                replaceBox,
                caseSensitiveBox,
            },
        };

        var errorText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 232, 17, 35)),
            Visibility = Visibility.Collapsed,
        };
        var previewText = new TextBlock { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };

        // Returns null (with error set) for an unusable regex, or null (with no error) for a regex
        // box that's simply still empty - both leave that item's name untouched.
        string? ComputeNewName(FileSystemItem item, int index, out string? error)
        {
            error = null;

            if (guidRadio.IsChecked == true)
            {
                return guidNames[index] + item.Extension;
            }

            if (regexRadio.IsChecked != true)
            {
                return RenamePatternService.Apply(patternBox.Text, item, index) + item.Extension;
            }

            if (string.IsNullOrEmpty(findBox.Text))
            {
                return null;
            }

            try
            {
                var options = caseSensitiveBox.IsChecked == true
                    ? System.Text.RegularExpressions.RegexOptions.None
                    : System.Text.RegularExpressions.RegexOptions.IgnoreCase;
                return System.Text.RegularExpressions.Regex.Replace(item.Name, findBox.Text, replaceBox.Text, options);
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return null;
            }
        }

        void UpdatePreview()
        {
            var lines = new List<string>();
            string? sharedError = null;

            foreach (var (item, i) in selection.Take(4).Select((item, i) => (item, i)))
            {
                var newName = ComputeNewName(item, i, out var error);
                if (error is not null)
                {
                    sharedError = error;
                    break;
                }

                lines.Add($"{item.Name}  ->  {newName ?? item.Name}");
            }

            errorText.Visibility = sharedError is null ? Visibility.Collapsed : Visibility.Visible;
            errorText.Text = sharedError is null ? string.Empty : $"Invalid pattern: {sharedError}";
            previewText.Text = sharedError is not null
                ? string.Empty
                : string.Join("\n", lines) + (selection.Count > 4 ? $"\n... and {selection.Count - 4} more" : string.Empty);
        }

        patternBox.TextChanged += (_, _) => UpdatePreview();
        findBox.TextChanged += (_, _) => UpdatePreview();
        replaceBox.TextChanged += (_, _) => UpdatePreview();
        caseSensitiveBox.Checked += (_, _) => UpdatePreview();
        caseSensitiveBox.Unchecked += (_, _) => UpdatePreview();

        void SwitchMode()
        {
            var isRegex = regexRadio.IsChecked == true;
            var isGuid = guidRadio.IsChecked == true;
            patternPanel.Visibility = !isRegex && !isGuid ? Visibility.Visible : Visibility.Collapsed;
            regexPanel.Visibility = isRegex ? Visibility.Visible : Visibility.Collapsed;
            guidPanel.Visibility = isGuid ? Visibility.Visible : Visibility.Collapsed;
            UpdatePreview();
        }

        patternRadio.Checked += (_, _) => SwitchMode();
        regexRadio.Checked += (_, _) => SwitchMode();
        guidRadio.Checked += (_, _) => SwitchMode();
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
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { patternRadio, regexRadio, guidRadio } },
                    patternPanel,
                    regexPanel,
                    guidPanel,
                    errorText,
                    previewText,
                },
            },
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var renamed = new List<(string Source, string Destination)>();

        for (int i = 0; i < selection.Count; i++)
        {
            var item = selection[i];
            var newName = ComputeNewName(item, i, out var error);
            if (error is not null || string.IsNullOrEmpty(newName))
            {
                continue;
            }

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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogWarning("PaneView.BatchRenameAsync", ex);
            }
        }

        if (renamed.Count > 0)
        {
            UndoService.Instance.Push(new MoveUndo(renamed));
        }

        ViewModel.Refresh();
    }

    /// F3 behavior. Called from MainWindow's global F3 KeyboardAccelerator (see RenameSelectionAsync).
    public async Task MoveSelectionToNewFolderAsync()
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
            LoggingService.LogWarning("PaneView.MoveSelectionToNewFolderAsync", ex);
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

    private async void CommitRename()
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

        if (item.IsRemote)
        {
            await RenameRemoteItemAsync(item, newName);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.LogWarning("PaneView.CommitRename", ex);
        }
    }

    /// No Undo support for remote rename (see CreateLinkUndo/RenameUndo etc - all local-only by
    /// design; a remote-touching operation never pushes an UndoAction).
    private async Task RenameRemoteItemAsync(FileSystemItem item, string newName)
    {
        if (!RemotePathService.TryParse(item.FullPath, out _, out var connectionId, out var remotePath) || ViewModel is null)
        {
            return;
        }

        var session = RemoteSessionManager.TryGetSession(connectionId);
        var parent = RemotePathService.GetParent(item.FullPath);
        if (session is null || parent is null)
        {
            return;
        }

        RemotePathService.TryParse(RemotePathService.Combine(parent, newName), out _, out _, out var newRemotePath);

        try
        {
            await session.RenameAsync(remotePath, newRemotePath, CancellationToken.None);
            ViewModel.Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            await ShowErrorAsync("Couldn't rename", ex.Message);
        }
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

            var op = FileOperationService.DetermineDropOperation(sourcePaths, targetFolder, IsAltPressed());

            e.AcceptedOperation = op == FileDropOperation.Move ? DataPackageOperation.Move : DataPackageOperation.Copy;
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.Caption = FileOperationService.DropCaption(op, targetFolder);
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

            var op = FileOperationService.DetermineDropOperation(sourcePaths, targetFolder, IsAltPressed());

            FileOperationQueueService.Current?.Enqueue(sourcePaths, targetFolder, op);
        }
        finally
        {
            SetDropHighlight(null);
            deferral.Complete();
        }
    }

    private void ItemsList_DragLeave(object sender, DragEventArgs e) => SetDropHighlight(null);

    private Windows.Foundation.Point? _marqueeStart;
    private bool _marqueeActive;

    /// Starts a drag-rectangle multi-select when the pointer goes down on empty space (not on an
    /// item - that's the native ListView click/drag-reorder path). No Ctrl/Shift-additive support
    /// yet: a marquee always replaces the current selection, matching Explorer's default drag.
    private void ItemsList_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ItemsList);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (FindContainerUnderPoint(point.Position) is not null)
        {
            return;
        }

        _marqueeStart = point.Position;
        _marqueeActive = false;
        ItemsList.CapturePointer(e.Pointer);

        // Clicking empty space (as opposed to an item container) doesn't give the ListView
        // keyboard focus on its own - grab it explicitly so Delete/Shift+Del work once the drag
        // that's about to start finishes selecting something.
        ItemsList.Focus(FocusState.Programmatic);
    }

    private void ItemsList_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_marqueeStart is not { } start)
        {
            return;
        }

        var point = e.GetCurrentPoint(ItemsList).Position;

        if (!_marqueeActive)
        {
            if (!MarqueeGeometry.ExceedsDragThreshold(start.X, start.Y, point.X, point.Y))
            {
                return;
            }

            _marqueeActive = true;
            MarqueeRect.Visibility = Visibility.Visible;
            ItemsList.SelectedItems.Clear();
        }

        var (x, y, w, h) = MarqueeGeometry.ComputeRect(start.X, start.Y, point.X, point.Y);

        Canvas.SetLeft(MarqueeRect, x);
        Canvas.SetTop(MarqueeRect, y);
        MarqueeRect.Width = w;
        MarqueeRect.Height = h;

        UpdateMarqueeSelection(new Windows.Foundation.Rect(x, y, w, h));
    }

    private void ItemsList_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_marqueeStart is not null)
        {
            ItemsList.ReleasePointerCapture(e.Pointer);
        }

        // Belt-and-suspenders with the Focus() call in PointerPressed above - a marquee drag never
        // clicks an item container, so the ListView never gets keyboard focus on its own, and
        // without this Delete/Shift+Del silently go nowhere right after a rubber-band multi-select.
        if (_marqueeActive)
        {
            ItemsList.Focus(FocusState.Programmatic);
        }

        _marqueeStart = null;
        _marqueeActive = false;
        MarqueeRect.Visibility = Visibility.Collapsed;
    }

    private void UpdateMarqueeSelection(Windows.Foundation.Rect marqueeRectOnItemsList)
    {
        var intersecting = FindContainersIntersecting(marqueeRectOnItemsList)
            .Select(c => c.Content).OfType<FileSystemItem>().ToList();

        ItemsList.SelectedItems.Clear();
        foreach (var item in intersecting)
        {
            ItemsList.SelectedItems.Add(item);
        }
    }

    private IEnumerable<ListViewItem> FindContainersIntersecting(Windows.Foundation.Rect rectOnItemsList)
    {
        foreach (var item in ItemsList.Items)
        {
            if (ItemsList.ContainerFromItem(item) is not ListViewItem container)
            {
                continue;
            }

            var bounds = container.TransformToVisual(ItemsList)
                .TransformBounds(new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight));

            if (MarqueeGeometry.Intersects(
                    bounds.X, bounds.Y, bounds.Width, bounds.Height,
                    rectOnItemsList.X, rectOnItemsList.Y, rectOnItemsList.Width, rectOnItemsList.Height))
            {
                yield return container;
            }
        }
    }

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

        // Local-only concepts (Open with..., Move to folder..., archive compress/extract,
        // Favourites/sync/watch/tags, Properties) are hidden entirely for a remote selection
        // rather than half-working - see the FTP/SFTP plan's explicit v1 scope cuts. Open, Cut/
        // Copy, Rename, Checksum, and Delete are all remote-aware (BeginRename/CommitRename,
        // ComputeHashesAsync, DeleteItemsAsync) and stay available.
        var isRemote = selection.Any(item => item.IsRemote);

        if (selection.Count == 1)
        {
            var single = selection[0];
            menu.Items.Add(NewMenuItem("Open", "", () => OpenItem(single)));
            if (!single.IsDirectory && !isRemote)
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

        if (!isRemote) {
        menu.Items.Add(NewMenuItem("Move to folder...", "", async () => await MoveSelectionToNewFolderAsync()));
        menu.Items.Add(NewMenuItem("Compress to .zip", "", async () => await CompressSelectionAsync(selection)));
        if (selection.Count > 0 && selection.All(item => IconHelper.IsExtractableArchive(item.Extension)))
        {
            menu.Items.Add(NewMenuItem("Extract", "", async () => await ExtractZipsAsync(selection)));
        }
        }
        if (selection.Count == 1 && selection[0].IsDirectory && !isRemote)
        {
            var folder = selection[0];
            menu.Items.Add(NewMenuItem(
                FavouriteService.IsFavourite(folder.FullPath) ? "Remove from Favourites" : "Add to Favourites",
                "",
                () => ToggleFavourite(folder.FullPath)));

            var settings = SettingsService.Current;
            if (settings.EnableSyncTasks)
            {
            menu.Items.Add(NewMenuItem("Set sync source...", "", () => SyncTaskService.SetPendingSource(folder.FullPath)));

            if (SyncTaskService.PendingSourcePath is { } pendingSource &&
                !string.Equals(pendingSource, folder.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                menu.Items.Add(NewMenuItem("Set sync target", "", async () => await SetSyncTargetAsync(folder.FullPath)));
            }
            }

            if (settings.EnableFolderWatching && settings.EnableScripting)
            {
                var existingWatch = WatchService.Tasks.FirstOrDefault(
                    t => string.Equals(t.FolderPath, folder.FullPath, StringComparison.OrdinalIgnoreCase));

                menu.Items.Add(existingWatch is not null
                    ? NewMenuItem("Stop watching folder", string.Empty, () => WatchService.RemoveTask(existingWatch.Id))
                    : NewMenuItem("Watch this folder...", string.Empty, async () => await WatchFolderAsync(folder.FullPath)));
            }
        }

        menu.Items.Add(NewMenuItem("Checksum...", "", async () => await ComputeHashesAsync(selection)));
        if (!isRemote)
        {
            menu.Items.Add(BuildTagSubMenu(selection));
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(NewMenuItem("Delete", "", async () => await DeleteItemsAsync(selection, permanent: false)));

        if (!isRemote) {
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(NewMenuItem("Properties", "", async () => await ShowPropertiesAsync(selection)));
        }

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
        if (ViewModel is not null && !RemotePathService.IsRemote(ViewModel.CurrentPath))
        {
        menu.Items.Add(NewMenuItem("New link...", string.Empty, async () => await CreateNewLinkAsync()));
        menu.Items.Add(NewMenuItem("Export folder listing (JSON)...", string.Empty, async () => await ExportFolderListingAsync()));
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(NewMenuItem("Refresh", "", () => ViewModel?.Refresh()));
        return menu;
    }

    private async void CreateNewFolderHere()
    {
        if (ViewModel is null)
        {
            return;
        }

        var basePath = ViewModel.CurrentPath;

        if (RemotePathService.IsRemote(basePath))
        {
            await CreateNewRemoteFolderAsync(basePath);
            return;
        }

        var candidate = FileOperationService.MakeUniqueDestination(Path.Combine(basePath, "New folder"));

        try
        {
            Directory.CreateDirectory(candidate);
            UndoService.Instance.Push(new CreateFolderUndo(candidate));
            ViewModel.Refresh(candidate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.LogWarning("PaneView.NewFolder", ex);
        }
    }

    /// No Undo support for remote folder creation (remote operations never push an UndoAction).
    private async Task CreateNewRemoteFolderAsync(string basePath)
    {
        if (!RemotePathService.TryParse(basePath, out _, out var connectionId, out _) || ViewModel is null)
        {
            return;
        }

        var session = RemoteSessionManager.TryGetSession(connectionId);
        if (session is null)
        {
            return;
        }

        var candidateFullPath = RemotePathService.Combine(basePath, "New folder");

        try
        {
            for (var i = 2; RemotePathService.TryParse(candidateFullPath, out _, out _, out var probeRemotePath) &&
                             await session.ExistsAsync(probeRemotePath, CancellationToken.None); i++)
            {
                candidateFullPath = RemotePathService.Combine(basePath, $"New folder ({i})");
            }

            RemotePathService.TryParse(candidateFullPath, out _, out _, out var finalRemotePath);
            await session.CreateDirectoryAsync(finalRemotePath, CancellationToken.None);
            ViewModel.Refresh(candidateFullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            await ShowErrorAsync("Couldn't create folder", ex.Message);
        }
    }

    private async Task CreateNewLinkAsync()
    {
        if (ViewModel is null)
        {
            return;
        }

        var nameBox = new TextBox { PlaceholderText = "Link name" };
        var targetBox = new TextBox { PlaceholderText = "Full path to the target file or folder" };
        var symlinkRadio = new RadioButton { Content = "Symbolic link", GroupName = "NewLinkType", IsChecked = true };
        var junctionRadio = new RadioButton { Content = "Junction (folders only - no admin rights or Developer Mode needed)", GroupName = "NewLinkType" };

        var dialog = new ContentDialog
        {
            Title = "New Link",
            Content = new StackPanel
            {
                Spacing = 8,
                Children = { nameBox, targetBox, symlinkRadio, junctionRadio },
            },
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var name = nameBox.Text.Trim();
        var target = targetBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        var targetIsDirectory = Directory.Exists(target);
        if (!targetIsDirectory && !File.Exists(target))
        {
            await ShowErrorAsync("Link target not found", $"\"{target}\" doesn't exist.");
            return;
        }

        var linkPath = Path.Combine(ViewModel.CurrentPath, name);
        if (Directory.Exists(linkPath) || File.Exists(linkPath))
        {
            await ShowErrorAsync("Name already in use", $"\"{name}\" already exists in this folder.");
            return;
        }

        LinkCreationResult result;
        if (junctionRadio.IsChecked == true)
        {
            if (!targetIsDirectory)
            {
                await ShowErrorAsync("Junctions need a folder target",
                    "Junctions can only point at folders - pick Symbolic link instead for a file target.");
                return;
            }

            result = await ReparsePointService.CreateJunctionAsync(linkPath, target);
        }
        else
        {
            result = ReparsePointService.CreateSymbolicLink(linkPath, target, targetIsDirectory);
        }

        if (!result.Success)
        {
            await ShowErrorAsync("Couldn't create the link", result.ErrorMessage ?? "Unknown error.");
            return;
        }

        UndoService.Instance.Push(new CreateLinkUndo(linkPath));
        ViewModel.Refresh(linkPath);
    }

    private async Task ExportFolderListingAsync()
    {
        if (ViewModel is null)
        {
            return;
        }

        try
        {
            var exportPath = FolderExportService.Export(ViewModel.CurrentPath, ViewModel.Items.ToList());
            ViewModel.Refresh(exportPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowErrorAsync("Couldn't export folder listing", ex.Message);
        }
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
        };

        await dialog.ShowAsync();
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
