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
        VideoPreview.Visibility = Visibility.Collapsed;
        VideoPreview.MediaPlayer?.Pause();
        VideoPreview.Source = null;
        PdfPreview.Visibility = Visibility.Collapsed;
        if (PdfPreview.CoreWebView2 is not null)
        {
            PdfPreview.CoreWebView2.Navigate("about:blank");
        }

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

        if (!item.IsDirectory && IconHelper.IsPreviewableVideo(item.Extension))
        {
            try
            {
                VideoPreview.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(item.FullPath));
                VideoPreview.Visibility = Visibility.Visible;
                return;
            }
            catch (Exception ex) when (ex is IOException or UriFormatException)
            {
                // fall through to icon preview
            }
        }

        if (!item.IsDirectory && IconHelper.IsPreviewablePdf(item.Extension))
        {
            try
            {
                await PdfPreview.EnsureCoreWebView2Async();
                PdfPreview.Source = new Uri(item.FullPath);
                PdfPreview.Visibility = Visibility.Visible;
                return;
            }
            catch (Exception)
            {
                // fall through to icon preview (e.g. WebView2 runtime not installed)
            }
        }

        if (!item.IsDirectory && IconHelper.IsPreviewableOffice(item.Extension))
        {
            TextPreview.Text = OfficeTextExtractor.Extract(item.FullPath, item.Extension);
            TextScroller.Visibility = Visibility.Visible;
            return;
        }

        IconGlyph.Glyph = item.Glyph;
        IconPreview.Visibility = Visibility.Visible;
    }
}
