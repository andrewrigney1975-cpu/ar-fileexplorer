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
        LocationMap.Visibility = Visibility.Collapsed;
        if (LocationMap.CoreWebView2 is not null)
        {
            LocationMap.CoreWebView2.Navigate("about:blank");
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

        if (metadata.Latitude is { } lat && metadata.Longitude is { } lon)
        {
            _ = ShowLocationMapAsync(item, lat, lon, metadata.Heading, metadata.FieldOfViewDegrees);
        }

        ImageMetadataPanel.Visibility = Visibility.Visible;
    }

    /// Renders an OpenStreetMap tile view with a marker (and, if the shot recorded one, a rotated
    /// heading indicator - a field-of-view wedge when FoV could be estimated, otherwise a plain
    /// direction arrow) via Leaflet loaded from its public CDN into the WebView2 already used for
    /// PDF preview - both OSM's tile server and Leaflet itself are free to use with no license/API key.
    private async Task ShowLocationMapAsync(FileSystemItem item, double latitude, double longitude, double? heading, double? fieldOfViewDegrees)
    {
        try
        {
            await LocationMap.EnsureCoreWebView2Async();
        }
        catch (Exception)
        {
            // WebView2 runtime not installed - map is a nice-to-have, silently skip it like PDF preview does.
            return;
        }

        if (!ReferenceEquals(ViewModel?.SelectedItem, item))
        {
            return;
        }

        var headingMarkerJs = heading is { } h
            ? BuildHeadingMarkerJs(latitude, longitude, h, fieldOfViewDegrees)
            : string.Empty;

        var html = $$"""
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8" />
                <link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css" />
                <style>
                    html, body, #map { margin: 0; padding: 0; height: 100%; width: 100%; }
                </style>
            </head>
            <body>
                <div id="map"></div>
                <script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>
                <script>
                    var map = L.map('map', { zoomControl: true, attributionControl: true })
                        .setView([{{latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, {{longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}}], 15);

                    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
                        maxZoom: 19,
                        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
                    }).addTo(map);

                    L.marker([{{latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, {{longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}}]).addTo(map);

                    {{headingMarkerJs}}
                </script>
            </body>
            </html>
            """;

        LocationMap.CoreWebView2.NavigateToString(html);
        LocationMap.Visibility = Visibility.Visible;
    }

    /// Leaflet's default marker pin is 41px tall - the ray length below is chosen so the wedge/arrow
    /// reads as clearly larger (~2x) than that pin, not just a same-size decoration next to it.
    private const double HeadingIconRayLengthPx = 90;

    /// Builds the JS for a rotated heading marker at the shot's location: a field-of-view wedge (two
    /// side edges only, no base line, per the app's design - a filled sector would misleadingly
    /// suggest a precisely-measured cone rather than an estimate) when a FoV could be estimated from
    /// the 35mm-equivalent focal length, otherwise a plain direction arrow.
    private static string BuildHeadingMarkerJs(double latitude, double longitude, double headingDegrees, double? fieldOfViewDegrees)
    {
        var lat = Inv(latitude);
        var lon = Inv(longitude);
        var heading = Inv(headingDegrees);

        if (fieldOfViewDegrees is not { } fov)
        {
            return $$"""
                var headingIcon = L.divIcon({
                    className: 'heading-icon',
                    html: '<div style="transform: rotate({{heading}}deg); width: 28px; height: 28px;">' +
                          '<svg width="28" height="28" viewBox="0 0 28 28">' +
                          '<polygon points="14,1 22,26 14,20 6,26" fill="#e3350d" stroke="white" stroke-width="1.5"/>' +
                          '</svg></div>',
                    iconSize: [28, 28],
                    iconAnchor: [14, 14],
                });
                L.marker([{{lat}}, {{lon}}], { icon: headingIcon, zIndexOffset: 1000 })
                    .addTo(map)
                    .bindTooltip('Camera heading: {{Inv0(headingDegrees)}}°');
                """;
        }

        // Wedge apex sits at the box center so CSS's default rotate-around-center transform-origin
        // keeps the apex pinned to the marker's geo position at every heading. The two rays sweep
        // ±halfFoV from "up" (0deg), matching the same clockwise-from-north convention as the CSS
        // rotate() below.
        const double box = 2 * HeadingIconRayLengthPx + 20;
        const double center = box / 2;
        var halfFovRad = fov / 2.0 * Math.PI / 180.0;
        var leftX = center - HeadingIconRayLengthPx * Math.Sin(halfFovRad);
        var rightX = center + HeadingIconRayLengthPx * Math.Sin(halfFovRad);
        var tipY = center - HeadingIconRayLengthPx * Math.Cos(halfFovRad);

        return $$"""
            var headingIcon = L.divIcon({
                className: 'heading-icon',
                html: '<div style="transform: rotate({{heading}}deg); width: {{Inv0(box)}}px; height: {{Inv0(box)}}px;">' +
                      '<svg width="{{Inv0(box)}}" height="{{Inv0(box)}}" viewBox="0 0 {{Inv0(box)}} {{Inv0(box)}}">' +
                      '<polygon points="{{Inv0(center)}},{{Inv0(center)}} {{Inv(leftX)}},{{Inv(tipY)}} {{Inv(rightX)}},{{Inv(tipY)}}" fill="#e3350d" fill-opacity="0.22" stroke="none"/>' +
                      '<line x1="{{Inv0(center)}}" y1="{{Inv0(center)}}" x2="{{Inv(leftX)}}" y2="{{Inv(tipY)}}" stroke="#e3350d" stroke-width="2.5"/>' +
                      '<line x1="{{Inv0(center)}}" y1="{{Inv0(center)}}" x2="{{Inv(rightX)}}" y2="{{Inv(tipY)}}" stroke="#e3350d" stroke-width="2.5"/>' +
                      '</svg></div>',
                iconSize: [{{Inv0(box)}}, {{Inv0(box)}}],
                iconAnchor: [{{Inv0(center)}}, {{Inv0(center)}}],
            });
            L.marker([{{lat}}, {{lon}}], { icon: headingIcon, zIndexOffset: 1000 })
                .addTo(map)
                .bindTooltip('Camera heading: {{Inv0(headingDegrees)}}°, field of view: ~{{Inv0(fov)}}°');
            """;
    }

    private static string Inv(double value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Inv0(double value) => value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

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
