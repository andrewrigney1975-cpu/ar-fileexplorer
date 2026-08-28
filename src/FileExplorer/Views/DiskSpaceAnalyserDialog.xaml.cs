using System.Security.AccessControl;
using System.Security.Principal;
using FileExplorer.Models;
using FileExplorer.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using UIPath = Microsoft.UI.Xaml.Shapes.Path;

namespace FileExplorer.Views;

public sealed partial class DiskSpaceAnalyserDialog : UserControl
{
    private const int MaxSlices = 12;
    private const double OuterRadius = 260;
    private const double InnerRadius = 140;

    private static readonly Color[] Palette =
    {
        Color.FromArgb(255, 0, 99, 177), Color.FromArgb(255, 16, 137, 62), Color.FromArgb(255, 255, 140, 0), Color.FromArgb(255, 196, 43, 28),
        Color.FromArgb(255, 136, 23, 152), Color.FromArgb(255, 0, 153, 188), Color.FromArgb(255, 122, 117, 116), Color.FromArgb(255, 239, 105, 80),
        Color.FromArgb(255, 0, 178, 148), Color.FromArgb(255, 90, 92, 214), Color.FromArgb(255, 186, 216, 10), Color.FromArgb(255, 184, 47, 138),
    };

    private List<SpaceEntry> _entries = new();
    private CancellationTokenSource? _cts;
    private string? _currentPath;
    private int _hoverToken;

    private sealed record DriveTile(string Label, string RootPath, double UsedPercent, string UsageText);

    private sealed record EntryRow(SpaceEntry Entry, string Name, string Glyph, string SizeDisplay);

    public Action? RequestClose { get; set; }

    /// Set before showing the dialog to skip the drive grid and jump straight into that path's
    /// breakdown - a drive root from "Analyse Disk", or any folder from "Analyse Folder...".
    public string? InitialDrivePath { get; set; }

    public DiskSpaceAnalyserDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (InitialDrivePath is { } path)
            {
                _ = NavigateToAsync(path);
            }
            else
            {
                PopulateDriveGrid();
            }
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => RequestClose?.Invoke();

    private void PopulateDriveGrid()
    {
        var tiles = DiskSpaceAnalyserService.GetDrives().Select(d =>
        {
            var percent = d.TotalBytes > 0 ? d.UsedBytes * 100.0 / d.TotalBytes : 0;
            var usageText = d.TotalBytes > 0
                ? $"{FileSystemItem.FormatSize(d.UsedBytes)} of {FileSystemItem.FormatSize(d.TotalBytes)} used ({percent:F0}%)"
                : "Usage unavailable";
            return new DriveTile(d.Label, d.Drive.RootDirectory.FullName, percent, usageText);
        }).ToList();

        DriveGridView.ItemsSource = tiles;
    }

    private void DriveGridView_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (DriveGridView.SelectedItem is DriveTile tile)
        {
            _ = NavigateToAsync(tile.RootPath);
        }
    }

    private void EntriesList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (EntriesList.SelectedItem is EntryRow row && row.Entry.IsDirectory)
        {
            _ = NavigateToAsync(row.Entry.FullPath);
        }
    }

    private void GoHome()
    {
        _cts?.Cancel();
        _currentPath = null;
        BreadcrumbPanel.Visibility = Visibility.Collapsed;
        ExportButton.Visibility = Visibility.Collapsed;
        BreakdownPanel.Visibility = Visibility.Collapsed;
        DriveGridPanel.Visibility = Visibility.Visible;
        PopulateDriveGrid();
    }

    private async Task NavigateToAsync(string path)
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;

        _currentPath = path;
        DriveGridPanel.Visibility = Visibility.Collapsed;
        BreakdownPanel.Visibility = Visibility.Visible;
        BreadcrumbPanel.Visibility = Visibility.Visible;
        ExportButton.Visibility = Visibility.Visible;
        LoadingText.Visibility = Visibility.Visible;
        BuildBreadcrumb(path);

        List<SpaceEntry> entries;
        try
        {
            entries = await DiskSpaceAnalyserService.AnalyseFolderAsync(path, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        LoadingText.Visibility = Visibility.Collapsed;
        _entries = entries;
        EntriesList.ItemsSource = entries.Select(e => new EntryRow(
            e,
            e.Name,
            e.IsDirectory ? IconHelper.Folder : IconHelper.GenericFile,
            e.IsDirectory ? $"{FileSystemItem.FormatSize(e.SizeBytes)} ({e.ItemCount} items)" : FileSystemItem.FormatSize(e.SizeBytes))).ToList();

        BuildChart(entries);
    }

    private void BuildBreadcrumb(string path)
    {
        BreadcrumbPanel.Children.Clear();

        var home = new Button { Content = "Home", Style = (Style)Application.Current.Resources["BreadcrumbButtonStyle"] };
        home.Click += (_, _) => GoHome();
        BreadcrumbPanel.Children.Add(home);
        AddBreadcrumbSeparator();

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
            button.Click += (_, _) => _ = NavigateToAsync(fullPath);
            BreadcrumbPanel.Children.Add(button);

            if (i < segments.Count - 1)
            {
                AddBreadcrumbSeparator();
            }
        }
    }

    private void AddBreadcrumbSeparator()
    {
        BreadcrumbPanel.Children.Add(new FontIcon
        {
            Glyph = "",
            FontSize = 10,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center,
        });
    }

    /// Hand-drawn donut: WinUI's SkiaSharp-based chart controls (e.g. LiveCharts2) render through
    /// SwapChainPanel/ANGLE, which has a known unresolved bug for unpackaged apps (mismatched
    /// libEGL/libGLESv2 build flags) that silently fails to acquire a GL context - blank chart, no
    /// exception. Plain Path geometry sidesteps that entirely since it's ordinary XAML rendering.
    private void BuildChart(List<SpaceEntry> entries)
    {
        ChartCanvas.Children.Clear();

        var top = entries.Take(MaxSlices).Where(e => e.SizeBytes > 0).ToList();
        var rest = entries.Skip(MaxSlices).ToList();
        var otherSize = rest.Sum(e => e.SizeBytes);

        var slices = top.Select(e => (Entry: (SpaceEntry?)e, e.Name, e.SizeBytes, GroupedCount: 0)).ToList();
        if (otherSize > 0)
        {
            slices.Add((null, "Other", otherSize, rest.Count));
        }

        var total = slices.Sum(s => s.SizeBytes);
        if (total <= 0)
        {
            return;
        }

        const double cx = 340;
        const double cy = 340;

        double startAngle = 0;
        var colorIndex = 0;

        foreach (var slice in slices)
        {
            var sweep = slice.SizeBytes * 360.0 / total;
            var color = slice.Entry is null ? Color.FromArgb(255, 140, 140, 140) : Palette[colorIndex++ % Palette.Length];

            var path = new UIPath
            {
                Data = BuildRingSegmentGeometry(cx, cy, OuterRadius, InnerRadius, startAngle, sweep),
                Fill = new SolidColorBrush(color),
                Tag = slice.Entry,
            };

            if (slice.Entry is { IsDirectory: true } entry)
            {
                path.DoubleTapped += (_, _) => _ = NavigateToAsync(entry.FullPath);
            }

            path.PointerEntered += (_, e) => ShowHoverPopover(slice.Entry, slice.Name, slice.SizeBytes, slice.GroupedCount, e);
            path.PointerMoved += (_, e) => PositionHoverPopover(e);
            path.PointerExited += (_, _) =>
            {
                _hoverToken++;
                HoverPopover.Visibility = Visibility.Collapsed;
            };

            ChartCanvas.Children.Add(path);

            // Only label slices wide enough to read - a sliver's label would just overlap its neighbors.
            if (sweep >= 360.0 * 0.03)
            {
                var midAngle = startAngle + sweep / 2;
                var labelRadius = (OuterRadius + InnerRadius) / 2;
                var (lx, ly) = PointOnCircle(cx, cy, labelRadius, midAngle);

                var label = new TextBlock
                {
                    Text = $"{slice.Name}\n{FileSystemItem.FormatSize(slice.SizeBytes)}",
                    FontSize = 11,
                    TextAlignment = TextAlignment.Center,
                    Foreground = new SolidColorBrush(Colors.White),
                    Width = 100,
                    TextWrapping = TextWrapping.Wrap,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(label, lx - 50);
                Canvas.SetTop(label, ly - 14);
                ChartCanvas.Children.Add(label);
            }

            startAngle += sweep;
        }

        var centerText = new TextBlock
        {
            Text = FileSystemItem.FormatSize(total),
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            Width = InnerRadius * 2 - 20,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(centerText, cx - InnerRadius + 10);
        Canvas.SetTop(centerText, cy - 10);
        ChartCanvas.Children.Add(centerText);

        // Clear() above also dropped the popover from the canvas - re-add it on top of the slices.
        ChartCanvas.Children.Add(HoverPopover);
    }

    private void PositionHoverPopover(PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(ChartCanvas).Position;
        Canvas.SetLeft(HoverPopover, Math.Clamp(pos.X + 16, 0, 680 - 260));
        Canvas.SetTop(HoverPopover, Math.Clamp(pos.Y + 16, 0, 680 - 170));
    }

    private async void ShowHoverPopover(SpaceEntry? entry, string label, long sizeBytes, int groupedCount, PointerRoutedEventArgs e)
    {
        var token = ++_hoverToken;

        PositionHoverPopover(e);
        HoverName.Text = label;
        HoverKind.Text = entry is null ? $"{groupedCount} smaller item{(groupedCount == 1 ? "" : "s")}, grouped" : (entry.IsDirectory ? "Folder" : "File");
        HoverSize.Text = entry is { IsDirectory: true }
            ? $"{FileSystemItem.FormatSize(sizeBytes)} ({entry.ItemCount} items)"
            : FileSystemItem.FormatSize(sizeBytes);
        HoverAttributes.Text = string.Empty;
        HoverCreated.Text = string.Empty;
        HoverModified.Text = string.Empty;
        HoverOwner.Text = string.Empty;
        HoverPopover.Visibility = Visibility.Visible;

        if (entry is null)
        {
            return;
        }

        var details = await Task.Run(() => GetEntryDetails(entry.FullPath, entry.IsDirectory));

        // The pointer may have already left this slice (or entered another) by the time this
        // finishes - a stale result must not overwrite whatever's showing now.
        if (token != _hoverToken)
        {
            return;
        }

        HoverAttributes.Text = $"Attributes: {(string.IsNullOrEmpty(details.Attributes) ? "-" : details.Attributes)}";
        HoverCreated.Text = $"Created: {FileSystemItem.FormatDate(details.Created)}";
        HoverModified.Text = $"Modified: {FileSystemItem.FormatDate(details.Modified)}";
        HoverOwner.Text = $"Owner: {details.Owner ?? "Unknown"}";
    }

    private static (string Attributes, DateTime Created, DateTime Modified, string? Owner) GetEntryDetails(string path, bool isDirectory)
    {
        var attributesDisplay = string.Empty;
        var created = DateTime.MinValue;
        var modified = DateTime.MinValue;
        string? owner = null;

        try
        {
            attributesDisplay = FileSystemItem.FormatAttributes(File.GetAttributes(path));
            created = File.GetCreationTime(path);
            modified = File.GetLastWriteTime(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        try
        {
            FileSystemSecurity security = isDirectory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            owner = (security.GetOwner(typeof(NTAccount)) as NTAccount)?.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
        }

        return (attributesDisplay, created, modified, owner);
    }

    private static (double X, double Y) PointOnCircle(double cx, double cy, double radius, double angleDeg)
    {
        var rad = (angleDeg - 90) * Math.PI / 180.0;
        return (cx + radius * Math.Cos(rad), cy + radius * Math.Sin(rad));
    }

    /// A donut "slice" as a filled ring segment: outer arc, straight edge in, inner arc back,
    /// straight edge closing the shape. Angles are degrees clockwise from 12 o'clock.
    private static PathGeometry BuildRingSegmentGeometry(double cx, double cy, double outerR, double innerR, double startAngle, double sweep)
    {
        sweep = Math.Min(sweep, 359.9);
        var endAngle = startAngle + sweep;
        var isLargeArc = sweep > 180;

        var outerStart = PointOnCircle(cx, cy, outerR, startAngle);
        var outerEnd = PointOnCircle(cx, cy, outerR, endAngle);
        var innerEnd = PointOnCircle(cx, cy, innerR, endAngle);
        var innerStart = PointOnCircle(cx, cy, innerR, startAngle);

        var figure = new PathFigure { StartPoint = new Windows.Foundation.Point(outerStart.X, outerStart.Y), IsClosed = true };
        figure.Segments.Add(new ArcSegment
        {
            Point = new Windows.Foundation.Point(outerEnd.X, outerEnd.Y),
            Size = new Windows.Foundation.Size(outerR, outerR),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = isLargeArc,
        });
        figure.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(innerEnd.X, innerEnd.Y) });
        figure.Segments.Add(new ArcSegment
        {
            Point = new Windows.Foundation.Point(innerStart.X, innerStart.Y),
            Size = new Windows.Foundation.Size(innerR, innerR),
            SweepDirection = SweepDirection.Counterclockwise,
            IsLargeArc = isLargeArc,
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPath is null)
        {
            return;
        }

        try
        {
            DiskSpaceAnalyserService.Export(_currentPath, _entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // This dialog only ever runs hosted inside MainWindow's own already-open ContentDialog,
            // so a nested ContentDialog.ShowAsync() here throws ("Only a single ContentDialog can be
            // open at any time") - use a Flyout instead, same fix as ScriptManagerDialog/ControlCentreDialog.
            var flyout = new Flyout { Placement = FlyoutPlacementMode.Bottom };
            flyout.Content = new StackPanel
            {
                Spacing = 4,
                Width = 280,
                Children =
                {
                    new TextBlock { Text = "Couldn't export listing", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                    new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                },
            };
            flyout.ShowAt((FrameworkElement)sender);
        }
    }
}
