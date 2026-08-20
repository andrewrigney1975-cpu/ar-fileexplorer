using FileExplorer.Helpers;
using FileExplorer.Models;
using FileExplorer.Services;
using FileExplorer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FileExplorer.Views;

public sealed partial class PreviewPane : UserControl
{
    private sealed record ExifRow(string Label, string Value);

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(PaneViewModel), typeof(PreviewPane), new PropertyMetadata(null, OnViewModelChanged));

    public PreviewPane()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => Refresh(ViewModel?.SelectedItem);
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
        CodeScroller.Visibility = Visibility.Collapsed;
        IconPreview.Visibility = Visibility.Collapsed;
        VideoPreview.Visibility = Visibility.Collapsed;
        VideoPreview.MediaPlayer?.Pause();
        VideoPreview.Source = null;
        PdfPreview.Visibility = Visibility.Collapsed;
        if (PdfPreview.CoreWebView2 is not null)
        {
            PdfPreview.CoreWebView2.Navigate("about:blank");
        }
        ImageMetadataPanel.Visibility = Visibility.Collapsed;
        ExifScroller.Visibility = Visibility.Collapsed;

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
                // WIC/BitmapDecoder can't read AVIF without a separate OS codec install - decode via
                // libheif first, into the same PNG bytes a normal image's raw file bytes would be.
                byte[]? sourceBytes = null;
                if (string.Equals(item.Extension, ".avif", StringComparison.OrdinalIgnoreCase))
                {
                    sourceBytes = await AvifImageService.DecodeToPngAsync(item.FullPath, maxDimension: null);
                    if (sourceBytes is null)
                    {
                        throw new IOException("AVIF decode failed.");
                    }
                }
                else
                {
                    using var stream = File.OpenRead(item.FullPath);
                    using var memStream = new MemoryStream();
                    await stream.CopyToAsync(memStream);
                    sourceBytes = memStream.ToArray();
                }

                var bitmap = new BitmapImage();
                using var bytesStream = new MemoryStream(sourceBytes);
                await bitmap.SetSourceAsync(bytesStream.AsRandomAccessStream());
                ImagePreview.Source = bitmap;
                ImagePreview.Visibility = Visibility.Visible;

                _ = ShowImageMetadataAsync(item);
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
                var text = new string(buffer, 0, count);

                if (IconHelper.IsCodeExtension(item.Extension))
                {
                    ShowCodePreview(text, item.Extension);
                }
                else
                {
                    TextPreview.Text = text;
                    TextScroller.Visibility = Visibility.Visible;
                }
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

    /// Runs after the image bitmap itself is already showing, so the extra WIC/libheif metadata
    /// read never delays the visible preview - if the user has since selected something else, the
    /// stale result is just discarded instead of overwriting the new selection's details.
    private async Task ShowImageMetadataAsync(FileSystemItem item)
    {
        var metadata = await ImageMetadataService.ReadAsync(item.FullPath, item.Extension).ConfigureAwait(true);
        if (metadata is null || !ReferenceEquals(ViewModel?.SelectedItem, item))
        {
            return;
        }

        ImageDimensionsText.Text = $"{metadata.Width} × {metadata.Height} px · {metadata.Format}";
        ImageBitDepthText.Text = $"Bit depth: {metadata.BitDepth}";
        ImageColorModelText.Text = $"Color model: {metadata.ColorModel}";

        if (metadata.Exif.Count > 0)
        {
            ExifList.ItemsSource = metadata.Exif.Select(e => new ExifRow(e.Label, e.Value)).ToList();
            ExifScroller.Visibility = Visibility.Visible;
        }

        ImageMetadataPanel.Visibility = Visibility.Visible;
    }

    private void ShowCodePreview(string text, string extension)
    {
        var lines = SyntaxHighlighter.Tokenize(text, extension);
        var isDark = ActualTheme == ElementTheme.Dark;

        CodeLineNumbers.Text = string.Join('\n', Enumerable.Range(1, lines.Count));

        CodePreview.Blocks.Clear();
        foreach (var lineTokens in lines)
        {
            var paragraph = new Paragraph();
            foreach (var token in lineTokens)
            {
                paragraph.Inlines.Add(new Run { Text = token.Text, Foreground = BrushFor(token.Kind, isDark) });
            }
            if (lineTokens.Count == 0)
            {
                paragraph.Inlines.Add(new Run { Text = " " });
            }
            CodePreview.Blocks.Add(paragraph);
        }

        CodeScroller.Visibility = Visibility.Visible;
    }

    private static Brush BrushFor(SyntaxTokenKind kind, bool isDark)
    {
        var color = kind switch
        {
            SyntaxTokenKind.Keyword => isDark ? Windows.UI.Color.FromArgb(255, 86, 156, 214) : Windows.UI.Color.FromArgb(255, 0, 0, 255),
            SyntaxTokenKind.String => isDark ? Windows.UI.Color.FromArgb(255, 214, 157, 133) : Windows.UI.Color.FromArgb(255, 163, 21, 21),
            SyntaxTokenKind.Comment => isDark ? Windows.UI.Color.FromArgb(255, 96, 139, 78) : Windows.UI.Color.FromArgb(255, 0, 128, 0),
            SyntaxTokenKind.Number => isDark ? Windows.UI.Color.FromArgb(255, 181, 206, 168) : Windows.UI.Color.FromArgb(255, 9, 134, 88),
            SyntaxTokenKind.Tag => isDark ? Windows.UI.Color.FromArgb(255, 86, 156, 214) : Windows.UI.Color.FromArgb(255, 163, 21, 21),
            SyntaxTokenKind.Attribute => isDark ? Windows.UI.Color.FromArgb(255, 156, 220, 254) : Windows.UI.Color.FromArgb(255, 255, 0, 0),
            _ => isDark ? Windows.UI.Color.FromArgb(255, 220, 220, 220) : Windows.UI.Color.FromArgb(255, 30, 30, 30),
        };
        return new SolidColorBrush(color);
    }
}
