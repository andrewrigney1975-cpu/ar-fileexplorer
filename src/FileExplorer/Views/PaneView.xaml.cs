using System.Diagnostics;
using FileExplorer.Models;
using FileExplorer.Services;
using FileExplorer.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        if (ViewModel?.SelectedItem is not { } item)
        {
            return;
        }

        if (item.IsDirectory)
        {
            ViewModel.NavigateTo(item.FullPath);
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

    private static bool IsAltPressed()
    {
        var state = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu);
        return (state & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }
}
