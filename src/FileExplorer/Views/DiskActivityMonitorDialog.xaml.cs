using FileExplorer.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace FileExplorer.Views;

public sealed partial class DiskActivityMonitorDialog : UserControl
{
    // 240 samples at the 250ms refresh rate = a rolling minute of history per drive, matching what
    // a glance at the chart is actually useful for (Task Manager's own disk graph uses that window).
    private const int HistoryLength = 240;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);
    private static readonly Color ReadColor = Color.FromArgb(255, 16, 137, 62);
    private static readonly Color WriteColor = Color.FromArgb(255, 255, 140, 0);

    private sealed class DriveRow
    {
        public required string DriveLetter { get; init; }
        public required Canvas Chart { get; init; }
        public required TextBlock CurrentText { get; init; }
        public readonly Queue<double> ReadHistory = new();
        public readonly Queue<double> WriteHistory = new();
    }

    private readonly Dictionary<string, DriveRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _pollCts;
    private bool _isSampling;

    public Action? RequestClose { get; set; }

    public DiskActivityMonitorDialog()
    {
        InitializeComponent();
        BuildRows();

        // Deliberately not started/stopped from Loaded/Unloaded: BuildRows()'s Children/RowDefinitions
        // churn triggers a spurious Unloaded (confirmed via diagnostic logging - it fires within
        // milliseconds, before the control's own Loaded handler even finishes, while the dialog stays
        // open and fully functional) - a naive Unloaded-cancels-the-loop hookup kills polling almost
        // immediately after the first sample. Started once here in the constructor instead; the loop
        // checks the live IsLoaded state itself each iteration rather than trusting event timing, so
        // it still stops for a genuine close (via Close_Click) without depending on Unloaded firing
        // exactly once and only when real.
        _pollCts = new CancellationTokenSource();
        _ = PollLoopAsync(_pollCts.Token);
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (IsLoaded)
            {
                await TickAsync();
            }

            try
            {
                await Task.Delay(RefreshInterval, token);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _pollCts?.Cancel();
        RequestClose?.Invoke();
    }

    private void BuildRows()
    {
        RowsGrid.RowDefinitions.Clear();
        RowsGrid.Children.Clear();
        _rows.Clear();

        var drives = DiskSpaceAnalyserService.GetDrives();
        if (drives.Count == 0)
        {
            return;
        }

        // Aim to fit every drive without a scrollbar - divide a target height evenly among them,
        // but never squash a row below a usable minimum (the ScrollViewer around RowsGrid is the
        // fallback once a system has enough drives that the minimum no longer fits).
        const double targetTotalHeight = 720;
        const double minRowHeight = 46;
        var rowHeight = Math.Max(minRowHeight, targetTotalHeight / drives.Count);

        for (var i = 0; i < drives.Count; i++)
        {
            var drive = drives[i];
            var driveLetter = drive.Drive.RootDirectory.FullName.TrimEnd('\\');

            RowsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(rowHeight) });

            var labelPanel = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 0) };
            labelPanel.Children.Add(new TextBlock
            {
                Text = drive.Label,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var currentText = new TextBlock { FontSize = 11, Opacity = 0.75, Text = "R 0.0  W 0.0 MB/s" };
            labelPanel.Children.Add(currentText);
            Grid.SetRow(labelPanel, i);
            Grid.SetColumn(labelPanel, 0);
            RowsGrid.Children.Add(labelPanel);

            var chartHost = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 3, 0, 3),
            };
            var chart = new Canvas();
            chartHost.Child = chart;
            Grid.SetRow(chartHost, i);
            Grid.SetColumn(chartHost, 1);
            RowsGrid.Children.Add(chartHost);

            var driveRow = new DriveRow { DriveLetter = driveLetter, Chart = chart, CurrentText = currentText };
            _rows[driveLetter] = driveRow;

            // Canvas never reports a real ActualWidth/Height on its own (unlike Grid/Border, it
            // doesn't participate in stretch layout - its MeasureOverride ignores available space
            // entirely), so without this every chart silently draws nothing. Size it explicitly from
            // its actually-arranged container instead.
            chartHost.SizeChanged += (_, args) =>
            {
                chart.Width = args.NewSize.Width;
                chart.Height = args.NewSize.Height;
                RedrawChart(driveRow);
            };
        }
    }

    private async Task TickAsync()
    {
        if (_isSampling)
        {
            return;
        }

        _isSampling = true;
        try
        {
            var samples = await Task.Run(DiskActivityMonitorService.Sample);

            foreach (var sample in samples)
            {
                if (!_rows.TryGetValue(sample.DriveLetter, out var row))
                {
                    continue;
                }

                if (row.ReadHistory.Count >= HistoryLength)
                {
                    row.ReadHistory.Dequeue();
                    row.WriteHistory.Dequeue();
                }

                row.ReadHistory.Enqueue(Math.Max(0, sample.ReadMBps));
                row.WriteHistory.Enqueue(Math.Max(0, sample.WriteMBps));

                row.CurrentText.Text = $"R {sample.ReadMBps:0.0}  W {sample.WriteMBps:0.0} MB/s";
                RedrawChart(row);
            }
        }
        finally
        {
            _isSampling = false;
        }
    }

    private static void RedrawChart(DriveRow row)
    {
        var chart = row.Chart;
        // Width/Height, not ActualWidth/ActualHeight - we set these explicitly (Canvas doesn't
        // stretch-size itself) in the same synchronous callback that calls this, and ActualWidth/
        // ActualHeight only update on the next layout pass, so they'd still read stale/zero here.
        var width = chart.Width;
        var height = chart.Height;
        chart.Children.Clear();

        if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0 || row.ReadHistory.Count < 2)
        {
            return;
        }

        const double padding = 4;
        var plotHeight = height - padding * 2;
        var maxValue = Math.Max(1.0, Math.Max(row.ReadHistory.Max(), row.WriteHistory.Max()));

        AddLine(chart, row.ReadHistory, width, plotHeight, padding, maxValue, ReadColor);
        AddLine(chart, row.WriteHistory, width, plotHeight, padding, maxValue, WriteColor);

        var scaleLabel = new TextBlock
        {
            Text = $"{maxValue:0} MB/s",
            FontSize = 9,
            Opacity = 0.6,
        };
        Canvas.SetLeft(scaleLabel, 4);
        Canvas.SetTop(scaleLabel, 2);
        chart.Children.Add(scaleLabel);
    }

    private static void AddLine(Canvas chart, Queue<double> history, double width, double plotHeight, double padding, double maxValue, Color color)
    {
        var values = history.ToArray();
        var stepX = width / (HistoryLength - 1);
        // Right-align so the most recent sample always sits at the chart's right edge, same as
        // Task Manager's live graphs - the line grows in from the right rather than jumping around.
        var startIndex = HistoryLength - values.Length;

        var points = new PointCollection();
        for (var i = 0; i < values.Length; i++)
        {
            var x = (startIndex + i) * stepX;
            var y = padding + plotHeight - (values[i] / maxValue * plotHeight);
            points.Add(new Windows.Foundation.Point(x, y));
        }

        chart.Children.Add(new Polyline
        {
            Points = points,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 1.5,
        });
    }
}
