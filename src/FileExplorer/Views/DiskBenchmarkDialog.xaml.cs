using FileExplorer.Models;
using FileExplorer.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace FileExplorer.Views;

public sealed partial class DiskBenchmarkDialog : UserControl
{
    private static readonly string[] SizeLabelsOrder = { "4 MB", "64 MB", "1 GB" };

    private static readonly (string Pattern, string Direction, string Abbrev, Color Color)[] SeriesOrder =
    {
        ("Sequential", "Write", "SeqW", Color.FromArgb(255, 0, 99, 177)),
        ("Sequential", "Read", "SeqR", Color.FromArgb(255, 16, 137, 62)),
        ("Random", "Write", "RndW", Color.FromArgb(255, 255, 140, 0)),
        ("Random", "Read", "RndR", Color.FromArgb(255, 196, 43, 28)),
    };

    private readonly List<BenchmarkResult> _results = new();
    private CancellationTokenSource? _cts;
    private string? _currentDrive;
    private DriveHardwareInfo? _currentInfo;

    private sealed record DriveTile(string Label, string RootPath, double UsedPercent, string UsageText);

    public Action? RequestClose { get; set; }

    public DiskBenchmarkDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => PopulateDriveGrid();
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
            _ = StartBenchmarkAsync(tile.RootPath, tile.Label);
        }
    }

    private void ChangeDriveButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        DriveNameText.Visibility = Visibility.Collapsed;
        ChangeDriveButton.Visibility = Visibility.Collapsed;
        ExportButton.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = string.Empty;
        DriveGridPanel.Visibility = Visibility.Visible;
        PopulateDriveGrid();
    }

    private async Task StartBenchmarkAsync(string driveRoot, string driveLabel)
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;

        _currentDrive = driveRoot;
        _results.Clear();
        ChartCanvas.Children.Clear();
        InfoRows.Children.Clear();

        DriveGridPanel.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Visible;
        DriveNameText.Text = driveLabel;
        DriveNameText.Visibility = Visibility.Visible;
        ChangeDriveButton.Visibility = Visibility.Visible;
        ExportButton.Visibility = Visibility.Visible;
        ExportButton.IsEnabled = false;
        StatusText.Text = "Reading drive information...";

        var info = await Task.Run(() => DiskBenchmarkService.GetDriveHardwareInfo(driveRoot), token);
        if (token.IsCancellationRequested)
        {
            return;
        }

        _currentInfo = info;
        PopulateInfoPanel(info);

        var progress = new Progress<BenchmarkResult>(result =>
        {
            _results.Add(result);
            StatusText.Text = $"Running: {result.SizeLabel} {result.Pattern} {result.Direction} done...";
            RebuildChart();
        });

        StatusText.Text = "Running benchmark...";

        try
        {
            await DiskBenchmarkService.RunBenchmarkAsync(driveRoot, progress, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Benchmark failed: {ex.Message}";
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        StatusText.Text = "Done";
        ExportButton.IsEnabled = true;
    }

    private void PopulateInfoPanel(DriveHardwareInfo info)
    {
        InfoRows.Children.Clear();
        AddInfoRow("Manufacturer", info.Manufacturer ?? "Unknown");
        AddInfoRow("Model", info.Model ?? "Unknown");
        AddInfoRow("Capacity", info.CapacityBytes > 0 ? FileSystemItem.FormatSize(info.CapacityBytes) : "Unknown");
        AddInfoRow("Format", info.FileSystem ?? "Unknown");
        AddInfoRow("Interface", info.InterfaceType ?? "Unknown");
        AddInfoRow("Interface speed", info.InterfaceSpeed ?? "Unknown");
    }

    private void AddInfoRow(string label, string value)
    {
        InfoRows.Children.Add(new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = label, FontSize = 11, Opacity = 0.6 },
                new TextBlock { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap },
            },
        });
    }

    private void RebuildChart()
    {
        ChartCanvas.Children.Clear();
        if (_results.Count == 0)
        {
            return;
        }

        const double plotTop = 46, plotBottom = 620, leftMargin = 20, groupGap = 24, canvasWidth = 860;
        var plotHeight = plotBottom - plotTop;
        var barsPerGroup = SeriesOrder.Length;
        var groupCount = SizeLabelsOrder.Length;
        var availableWidth = canvasWidth - leftMargin * 2;
        var barsAreaWidth = availableWidth - groupGap * (groupCount - 1);
        var barSlotWidth = barsAreaWidth / (barsPerGroup * groupCount);
        var barWidth = barSlotWidth * 0.7;

        var maxValue = _results.Max(r => r.ThroughputMBps);
        if (maxValue <= 0)
        {
            maxValue = 1;
        }

        for (var g = 0; g < groupCount; g++)
        {
            var sizeLabel = SizeLabelsOrder[g];
            var groupStartX = leftMargin + g * (barsPerGroup * barSlotWidth + groupGap);

            for (var s = 0; s < barsPerGroup; s++)
            {
                var (pattern, direction, abbrev, color) = SeriesOrder[s];
                var result = _results.FirstOrDefault(r => r.SizeLabel == sizeLabel && r.Pattern == pattern && r.Direction == direction);
                var slotX = groupStartX + s * barSlotWidth;

                if (result is not null)
                {
                    var barHeight = Math.Max(2, result.ThroughputMBps / maxValue * plotHeight);
                    var bar = new Rectangle
                    {
                        Width = barWidth,
                        Height = barHeight,
                        Fill = new SolidColorBrush(color),
                        RadiusX = 3,
                        RadiusY = 3,
                    };
                    Canvas.SetLeft(bar, slotX + (barSlotWidth - barWidth) / 2);
                    Canvas.SetTop(bar, plotBottom - barHeight);
                    ChartCanvas.Children.Add(bar);

                    // A result that fell back to buffered I/O may be inflated by Windows' file
                    // cache rather than reflecting real disk speed - flagged visibly rather than
                    // presented as an equally-trustworthy number.
                    var valueLabel = new TextBlock
                    {
                        Text = result.Unbuffered ? result.ThroughputMBps.ToString("0") : $"{result.ThroughputMBps:0}*",
                        FontSize = 10,
                        TextAlignment = TextAlignment.Center,
                        Width = barSlotWidth,
                    };
                    if (!result.Unbuffered)
                    {
                        valueLabel.Foreground = new SolidColorBrush(Color.FromArgb(255, 232, 17, 35));
                        ToolTipService.SetToolTip(valueLabel, $"Fell back to cached (buffered) I/O for this test - may not reflect real disk speed: {result.FallbackReason}");
                    }
                    Canvas.SetLeft(valueLabel, slotX);
                    Canvas.SetTop(valueLabel, plotBottom - barHeight - 16);
                    ChartCanvas.Children.Add(valueLabel);
                }

                var abbrevLabel = new TextBlock
                {
                    Text = abbrev,
                    FontSize = 9,
                    Opacity = 0.7,
                    TextAlignment = TextAlignment.Center,
                    Width = barSlotWidth,
                };
                Canvas.SetLeft(abbrevLabel, slotX);
                Canvas.SetTop(abbrevLabel, plotBottom + 4);
                ChartCanvas.Children.Add(abbrevLabel);
            }

            var groupLabel = new TextBlock
            {
                Text = sizeLabel,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                Width = barsPerGroup * barSlotWidth,
            };
            Canvas.SetLeft(groupLabel, groupStartX);
            Canvas.SetTop(groupLabel, plotBottom + 20);
            ChartCanvas.Children.Add(groupLabel);
        }

        var title = new TextBlock
        {
            Text = "Throughput, in megabytes per second (MB/s)",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        Canvas.SetLeft(title, leftMargin);
        Canvas.SetTop(title, 0);
        ChartCanvas.Children.Add(title);

        if (_results.Any(r => !r.Unbuffered))
        {
            var warning = new TextBlock
            {
                Text = "* fell back to cached I/O for this test - hover the value for why",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 232, 17, 35)),
            };
            Canvas.SetLeft(warning, leftMargin + 260);
            Canvas.SetTop(warning, 1);
            ChartCanvas.Children.Add(warning);
        }

        var legendX = leftMargin;
        var legendY = 20.0;
        foreach (var (pattern, direction, abbrev, color) in SeriesOrder)
        {
            var swatch = new Rectangle { Width = 10, Height = 10, Fill = new SolidColorBrush(color) };
            Canvas.SetLeft(swatch, legendX);
            Canvas.SetTop(swatch, legendY + 2);
            ChartCanvas.Children.Add(swatch);

            var label = new TextBlock { Text = $"{abbrev} = {pattern} {direction} (MB/s)", FontSize = 10, Opacity = 0.7 };
            Canvas.SetLeft(label, legendX + 16);
            Canvas.SetTop(label, legendY);
            ChartCanvas.Children.Add(label);

            legendX += 170;
        }
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDrive is null || _currentInfo is null || _results.Count == 0)
        {
            return;
        }

        try
        {
            var path = DiskBenchmarkService.Export(_currentDrive, _currentInfo, _results);
            StatusText.Text = $"Exported to {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Couldn't export results",
                Content = ex.Message,
                CloseButtonText = "OK",
            }.ShowAsync();
        }
    }
}
