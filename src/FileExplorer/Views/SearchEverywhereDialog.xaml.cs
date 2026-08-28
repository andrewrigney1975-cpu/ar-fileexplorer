using FileExplorer.Models;
using FileExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FileExplorer.Views;

public sealed partial class SearchEverywhereDialog : UserControl
{
    private const int DebounceMs = 200;
    private const int MaxResults = 300;

    private sealed record ResultRow(string Path, string Name, string DirectoryPath, bool IsDirectory, string SizeDisplay, string ModifiedDisplay, string Glyph, string? RatingStars, double RatingOpacity);

    private CancellationTokenSource? _searchCts;
    private string _lastQuery = string.Empty;

    public Action? RequestClose { get; set; }

    /// (targetPath, selectPath) - navigates the active pane to targetPath and, if selectPath is
    /// non-null, selects it once loaded. A folder result navigates *into* itself (targetPath = the
    /// folder, selectPath = null); a file result navigates to its containing folder and selects the
    /// file (targetPath = DirectoryPath, selectPath = the file). Set by MainWindow, which owns the
    /// active pane.
    public Action<string, string?>? NavigateToResult { get; set; }

    public Action? OpenSearchIndexSettings { get; set; }

    public SearchEverywhereDialog()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            QueryBox.Focus(FocusState.Programmatic);
            SearchIndexService.StatusChanged += OnIndexStatusChanged;
            UpdateEmptyState();
        };

        Unloaded += (_, _) => SearchIndexService.StatusChanged -= OnIndexStatusChanged;
    }

    private void OnIndexStatusChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateEmptyState);

    private void UpdateEmptyState()
    {
        if (SearchIndexService.Roots.Count == 0)
        {
            StatusText.Text = string.Empty;
            EmptyStateText.Text = "No folders are indexed yet. Add one to start searching.";
            EmptyStatePanel.Visibility = ResultsList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        StatusText.Text = SearchIndexService.IsScanning
            ? $"Indexing... ({SearchIndexService.EntryCount:N0} entries so far)"
            : $"{SearchIndexService.EntryCount:N0} entries indexed.";

        if (string.IsNullOrEmpty(QueryBox.Text) && ResultsList.Items.Count == 0)
        {
            EmptyStateText.Text = "Start typing to search.";
            EmptyStatePanel.Visibility = Visibility.Visible;
        }
        else if (ResultsList.Items.Count == 0 && !string.IsNullOrEmpty(QueryBox.Text))
        {
            EmptyStateText.Text = $"No matches for \"{QueryBox.Text}\".";
            EmptyStatePanel.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyStatePanel.Visibility = Visibility.Collapsed;
        }
    }

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        var query = QueryBox.Text;
        _lastQuery = query;
        if (string.IsNullOrWhiteSpace(query))
        {
            ResultsList.ItemsSource = null;
            UpdateEmptyState();
            return;
        }

        _ = RunSearchAsync(query, cts.Token);
    }

    private void RatingFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastQuery))
        {
            return;
        }

        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _ = RunSearchAsync(_lastQuery, cts.Token);
    }

    /// 0 (Any) .. 5, matching RatingFilterCombo's item order.
    private int MinRating => RatingFilterCombo?.SelectedIndex ?? 0;

    private async Task RunSearchAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DebounceMs, cancellationToken);

            var entries = await SearchIndexService.SearchAsync(query, MaxResults, cancellationToken, MinRating);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var rows = entries.Select(entry => new ResultRow(
                entry.Path,
                entry.Name,
                entry.DirectoryPath,
                entry.IsDirectory,
                entry.IsDirectory ? string.Empty : FileSystemItem.FormatSize(entry.SizeBytes),
                FileSystemItem.FormatDate(entry.Modified.ToLocalTime()),
                entry.IsDirectory ? IconHelper.Folder : IconHelper.GlyphFor(Path.GetExtension(entry.Name)),
                Helpers.RatingFormat.ToStars(entry.Rating),
                entry.Rating is null ? 0 : 1.0))
                .ToList();

            ResultsList.ItemsSource = rows;
            if (rows.Count > 0)
            {
                ResultsList.SelectedIndex = 0;
            }

            UpdateEmptyState();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke - not an error.
        }
    }

    private void QueryBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            NavigateToSelected();
        }
        else if (e.Key == VirtualKey.Down && ResultsList.Items.Count > 0)
        {
            e.Handled = true;
            ResultsList.SelectedIndex = Math.Min(ResultsList.SelectedIndex + 1, ResultsList.Items.Count - 1);
            ResultsList.ScrollIntoView(ResultsList.SelectedItem);
        }
        else if (e.Key == VirtualKey.Up && ResultsList.Items.Count > 0)
        {
            e.Handled = true;
            ResultsList.SelectedIndex = Math.Max(ResultsList.SelectedIndex - 1, 0);
            ResultsList.ScrollIntoView(ResultsList.SelectedItem);
        }
    }

    private void ResultsList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            NavigateToSelected();
        }
    }

    private void ResultsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => NavigateToSelected();

    private void NavigateToSelected()
    {
        if (ResultsList.SelectedItem is not ResultRow row)
        {
            return;
        }

        if (row.IsDirectory)
        {
            NavigateToResult?.Invoke(row.Path, null);
        }
        else
        {
            NavigateToResult?.Invoke(row.DirectoryPath, row.Path);
        }
    }

    private void ManageIndex_Click(object sender, RoutedEventArgs e) => OpenSearchIndexSettings?.Invoke();

    private void Close_Click(object sender, RoutedEventArgs e) => RequestClose?.Invoke();
}
