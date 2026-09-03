using FileExplorer.Models;
using FileExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FileExplorer.Views;

public sealed partial class ConvertToDialog : ContentDialog
{
    private readonly IReadOnlyList<string> _selection;

    public ConvertToDialog(IReadOnlyList<string> selectionPaths)
    {
        InitializeComponent();

        _selection = selectionPaths;

        FormatCombo.ItemsSource = ImageConversionService.Targets;
        FormatCombo.SelectedIndex = 0;

        DepthPanel.Visibility = selectionPaths.Any(Directory.Exists) ? Visibility.Visible : Visibility.Collapsed;
        DepthDirect.Checked += (_, _) => UpdateCount();
        DepthRecurse.Checked += (_, _) => UpdateCount();

        UpdateCount();
    }

    /// Set when the user confirms; null if they cancelled.
    public ConversionOptions? Options { get; private set; }

    public IReadOnlyList<string> ResolvedSources { get; private set; } = Array.Empty<string>();

    private FolderScanDepth Depth =>
        DepthRecurse.IsChecked == true ? FolderScanDepth.Recurse : FolderScanDepth.DirectChildrenOnly;

    private void FormatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var lossy = (FormatCombo.SelectedItem as ConversionFormat)?.IsLossy == true;
        QualityPanel.Visibility = lossy ? Visibility.Visible : Visibility.Collapsed;
        UpdateCount();
    }

    private void QualitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) =>
        QualityValue.Text = ((int)e.NewValue).ToString();

    private void UpdateCount()
    {
        if (FormatCombo.SelectedItem is not ConversionFormat target)
        {
            return;
        }

        ResolvedSources = ImageConversionService.ResolveSources(_selection, Depth);
        var convertible = ResolvedSources.Count(p =>
            !string.Equals(Path.GetExtension(p), target.Extension, StringComparison.OrdinalIgnoreCase));

        CountText.Text = ResolvedSources.Count == 0
            ? "No convertible image files found in the selection."
            : $"{convertible} image file(s) will be converted to {target.Display}" +
              (convertible < ResolvedSources.Count
                  ? $" ({ResolvedSources.Count - convertible} already in that format will be skipped)."
                  : ".");

        IsPrimaryButtonEnabled = convertible > 0;
    }

    /// Snapshots the chosen options; call after ShowAsync returns Primary.
    public void Capture()
    {
        if (FormatCombo.SelectedItem is not ConversionFormat target)
        {
            return;
        }

        var post = PostDelete.IsChecked == true ? PostConversionAction.DeleteOriginal
            : PostMove.IsChecked == true ? PostConversionAction.MoveToOriginals
            : PostConversionAction.KeepOriginal;

        Options = new ConversionOptions(target, Depth, post, (int)QualitySlider.Value);
        ResolvedSources = ImageConversionService.ResolveSources(_selection, Depth);
    }
}
