using FileExplorer.Models;
using FileExplorer.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FileExplorer.Views;

/// The drive-selection grid from the Disk Space Analyser, reused as the fixed left pane of the
/// "Home" workspace. Picking a drive raises <see cref="DriveInvoked"/> with its root path.
public sealed partial class DrivePickerView : UserControl
{
    private sealed record DriveTile(string Label, string RootPath, double UsedPercent, string UsageText);

    public event EventHandler<string>? DriveInvoked;

    public DrivePickerView()
    {
        InitializeComponent();
        Loaded += (_, _) => Populate();
    }

    /// Re-reads drive usage - call when the app regains focus so free-space figures stay current.
    public void Refresh() => Populate();

    private void Populate()
    {
        DriveGridView.ItemsSource = DiskSpaceAnalyserService.GetDrives().Select(d =>
        {
            var percent = d.TotalBytes > 0 ? d.UsedBytes * 100.0 / d.TotalBytes : 0;
            var usageText = d.TotalBytes > 0
                ? $"{FileSystemItem.FormatSize(d.UsedBytes)} of {FileSystemItem.FormatSize(d.TotalBytes)} used ({percent:F0}%)"
                : "Usage unavailable";
            return new DriveTile(d.Label, d.Drive.RootDirectory.FullName, percent, usageText);
        }).ToList();
    }

    private void DriveGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DriveTile tile)
        {
            DriveInvoked?.Invoke(this, tile.RootPath);
        }
    }

    private void DriveGridView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (DriveGridView.SelectedItem is DriveTile tile)
        {
            DriveInvoked?.Invoke(this, tile.RootPath);
        }
    }
}
