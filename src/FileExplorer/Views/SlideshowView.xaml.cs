using FileExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace FileExplorer.Views;

/// A full-bleed image slideshow (Quick Look's bigger sibling): cursor keys walk the folder's
/// images, a thumbnail strip below tracks the position, Space/Esc closes. Hosted in an app-level
/// popup by MainWindow.
public sealed partial class SlideshowView : UserControl
{
    private const double StripItemSize = 72;

    private IReadOnlyList<string> _paths = Array.Empty<string>();
    private readonly List<Border> _stripItems = new();
    private int _index;
    private int _loadToken;

    public event EventHandler? CloseRequested;

    public SlideshowView()
    {
        InitializeComponent();
    }

    public void Load(IReadOnlyList<string> imagePaths, int startIndex)
    {
        _paths = imagePaths;
        _index = Math.Clamp(startIndex, 0, Math.Max(0, imagePaths.Count - 1));

        StripPanel.Children.Clear();
        _stripItems.Clear();

        for (var i = 0; i < imagePaths.Count; i++)
        {
            var slot = i;
            var border = new Border
            {
                Width = StripItemSize,
                Height = StripItemSize,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Background = (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
                Child = new FontIcon { Glyph = IconHelper.Image, FontSize = 18, Opacity = 0.5 },
            };
            border.Tapped += (_, _) => ShowIndex(slot);
            _stripItems.Add(border);
            StripPanel.Children.Add(border);
            _ = LoadStripThumbAsync(slot);
        }

        ShowIndex(_index);
    }

    /// Called after the hosting popup opens so cursor keys land here.
    public void FocusForKeys() => Root.Focus(FocusState.Programmatic);

    public void Release()
    {
        _loadToken++;
        MainImage.Source = null;
        foreach (var item in _stripItems)
        {
            item.Child = null;
        }
        _stripItems.Clear();
        StripPanel.Children.Clear();
        _paths = Array.Empty<string>();
    }

    private void ShowIndex(int target)
    {
        if (_paths.Count == 0)
        {
            return;
        }

        _index = (target % _paths.Count + _paths.Count) % _paths.Count; // wrap around
        CaptionText.Text = $"{Path.GetFileName(_paths[_index])}   ({_index + 1} / {_paths.Count})";

        for (var i = 0; i < _stripItems.Count; i++)
        {
            _stripItems[i].BorderBrush = i == _index
                ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        ScrollStripToCurrent();

        var token = ++_loadToken;
        _ = LoadMainImageAsync(_paths[_index], token);
    }

    private async Task LoadMainImageAsync(string path, int token)
    {
        var bitmap = await DecodeAsync(path);
        if (token != _loadToken)
        {
            return;
        }

        MainImage.Source = bitmap;
        MainImage.Visibility = bitmap is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Visibility = bitmap is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadStripThumbAsync(int slot)
    {
        var path = _paths[slot];
        DateTimeOffset modified;
        try
        {
            modified = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var bitmap = await ThumbnailCacheService.GetOrCreateAsync(path, modified);
        if (bitmap is null || slot >= _stripItems.Count)
        {
            return;
        }

        _stripItems[slot].Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill };
    }

    private void ScrollStripToCurrent()
    {
        var stride = StripItemSize + 6; // width + StackPanel spacing
        var target = (_index + 0.5) * stride - StripScroller.ViewportWidth / 2 + 8; // 8 = panel padding
        StripScroller.ChangeView(Math.Max(0, target), null, null, disableAnimation: false);
    }

    private static async Task<BitmapImage?> DecodeAsync(string path)
    {
        try
        {
            byte[] bytes;
            if (string.Equals(Path.GetExtension(path), ".avif", StringComparison.OrdinalIgnoreCase))
            {
                bytes = await AvifImageService.DecodeToPngAsync(path, maxDimension: null)
                    ?? throw new IOException("AVIF decode failed.");
            }
            else
            {
                bytes = await File.ReadAllBytesAsync(path);
            }

            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(bytes);
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => ShowIndex(_index - 1);

    private void Next_Click(object sender, RoutedEventArgs e) => ShowIndex(_index + 1);

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Left:
            case VirtualKey.Up:
            case VirtualKey.PageUp:
                e.Handled = true;
                ShowIndex(_index - 1);
                break;
            case VirtualKey.Right:
            case VirtualKey.Down:
            case VirtualKey.PageDown:
                e.Handled = true;
                ShowIndex(_index + 1);
                break;
            case VirtualKey.Home:
                e.Handled = true;
                ShowIndex(0);
                break;
            case VirtualKey.End:
                e.Handled = true;
                ShowIndex(_paths.Count - 1);
                break;
            case VirtualKey.Space:
            case VirtualKey.Escape:
                e.Handled = true;
                CloseRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }
}
