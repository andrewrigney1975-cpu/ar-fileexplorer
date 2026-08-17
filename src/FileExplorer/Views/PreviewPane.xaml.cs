using FileExplorer.Models;
using FileExplorer.Services;
using FileExplorer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FileExplorer.Views;

public sealed partial class PreviewPane : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(PaneViewModel), typeof(PreviewPane), new PropertyMetadata(null, OnViewModelChanged));

    public PreviewPane()
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
        var pane = (PreviewPane)d;

        if (e.OldValue is PaneViewModel oldVm)
        {
            oldVm.PropertyChanged -= pane.ViewModel_PropertyChanged;
        }

        if (e.NewValue is PaneViewModel newVm)
        {
            newVm.PropertyChanged += pane.ViewModel_PropertyChanged;
        }

        pane.Refresh((e.NewValue as PaneViewModel)?.SelectedItem);
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PaneViewModel.SelectedItem))
        {
            Refresh(ViewModel?.SelectedItem);
        }
    }

    private async void Refresh(FileSystemItem? item)
    {
        ImagePreview.Visibility = Visibility.Collapsed;
        TextScroller.Visibility = Visibility.Collapsed;
        IconPreview.Visibility = Visibility.Collapsed;

        if (item is null)
        {
            EmptyState.Visibility = Visibility.Visible;
            ContentState.Visibility = Visibility.Collapsed;
            DetailsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        ContentState.Visibility = Visibility.Visible;
        DetailsPanel.Visibility = Visibility.Visible;

        NameText.Text = item.Name;
        KindText.Text = item.Kind;
        SizeText.Text = item.IsDirectory ? string.Empty : item.SizeDisplay;
        ModifiedText.Text = "Modified: " + item.ModifiedDisplay;

        if (!item.IsDirectory && IconHelper.IsPreviewableImage(item.Extension))
        {
            try
            {
                var bitmap = new BitmapImage();
                using var stream = File.OpenRead(item.FullPath);
                using var memStream = new MemoryStream();
                await stream.CopyToAsync(memStream);
                memStream.Position = 0;
                await bitmap.SetSourceAsync(memStream.AsRandomAccessStream());
                ImagePreview.Source = bitmap;
                ImagePreview.Visibility = Visibility.Visible;
                return;
            }
            catch (IOException)
            {
                // fall through to icon preview
            }
        }

        if (!item.IsDirectory && IconHelper.IsPreviewableText(item.Extension))
        {
            try
            {
                using var stream = File.OpenText(item.FullPath);
                var buffer = new char[4000];
                var count = await stream.ReadAsync(buffer, 0, buffer.Length);
                TextPreview.Text = new string(buffer, 0, count);
                TextScroller.Visibility = Visibility.Visible;
                return;
            }
            catch (IOException)
            {
                // fall through to icon preview
            }
        }

        IconGlyph.Glyph = item.Glyph;
        IconPreview.Visibility = Visibility.Visible;
    }
}
