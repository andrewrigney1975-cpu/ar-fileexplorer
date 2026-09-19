using System.Data;
using System.Linq;
using CommunityToolkit.WinUI.UI.Controls;
using FileExplorer.Models;
using FileExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace FileExplorer.Views;

public sealed partial class AdvancedSearchDialog : UserControl
{
    private const int ValidateDebounceMs = 400;

    private SavedSearch? _current;
    private CancellationTokenSource? _validateCts;
    private CancellationTokenSource? _runCts;
    private Flyout? _autocompleteFlyout;
    private ListView? _autocompleteListView;
    private int _autocompleteWordStart;

    public Action? RequestClose { get; set; }

    /// (targetPath, selectPath) - same contract as SearchEverywhereDialog.NavigateToResult, invoked
    /// when a result row that came straight from Entries (has a Path column) is double-clicked.
    public Action<string, string?>? NavigateToResult { get; set; }

    /// Set by MainWindow when opened from the rail with a saved Sql-kind query - preloads and
    /// auto-runs it.
    public SavedSearch? InitialQuery { get; set; }

    public AdvancedSearchDialog()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            RefreshSavedList();

            if (InitialQuery is { Kind: SavedSearchKind.Sql } saved)
            {
                LoadQuery(saved);
                _ = RunQueryAsync();
            }
            else
            {
                NewQuery_Click(this, new RoutedEventArgs());
            }

            CodeBox.Focus(FocusState.Programmatic);
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => RequestClose?.Invoke();

    // ----- Editor: line numbers (mirrors ScriptManagerDialog) -----

    private void CodeBox_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateLineNumbers();

        if (FindDescendantScrollViewer(CodeBox) is { } scroller)
        {
            scroller.ViewChanged += (_, _) =>
                LineNumberScroll.ChangeView(null, scroller.VerticalOffset, null, disableAnimation: true);
        }
    }

    private void CodeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateLineNumbers();
        ScheduleValidate();
        UpdateAutocomplete();
    }

    private void UpdateLineNumbers()
    {
        var lineCount = Math.Max(1, CodeBox.Text.Count(c => c == '\n') + 1);
        LineNumbersText.Text = string.Join("\n", Enumerable.Range(1, lineCount));
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            if (FindDescendantScrollViewer(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // ----- Validation -----

    private void ScheduleValidate()
    {
        _validateCts?.Cancel();
        var cts = new CancellationTokenSource();
        _validateCts = cts;
        _ = ValidateAfterDelayAsync(CodeBox.Text, cts.Token);
    }

    private async Task ValidateAfterDelayAsync(string sql, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ValidateDebounceMs, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var result = AdvancedSearchQueryService.Validate(sql);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (result.IsValid)
        {
            ValidationText.Visibility = Visibility.Collapsed;
            ValidationText.Text = string.Empty;
        }
        else
        {
            ValidationText.Text = result.ErrorMessage;
            ValidationText.Visibility = Visibility.Visible;
        }
    }

    // ----- Autocomplete -----

    private static readonly string[] AutocompleteWords = AdvancedSearchQueryService.Keywords
        .Concat(AdvancedSearchQueryService.SchemaTablesAndColumns.Keys)
        .Concat(AdvancedSearchQueryService.SchemaTablesAndColumns.Values.SelectMany(c => c))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(w => w, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private void CodeBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_autocompleteFlyout is null)
        {
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            _autocompleteFlyout.Hide();
            e.Handled = true;
        }
        else if ((e.Key == VirtualKey.Tab || e.Key == VirtualKey.Enter) && _autocompleteListView?.SelectedItem is string word)
        {
            AcceptAutocomplete(word);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Down && _autocompleteListView is { Items.Count: > 0 } list)
        {
            list.SelectedIndex = Math.Min(list.SelectedIndex + 1, list.Items.Count - 1);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Up && _autocompleteListView is { Items.Count: > 0 } list2)
        {
            list2.SelectedIndex = Math.Max(list2.SelectedIndex - 1, 0);
            e.Handled = true;
        }
    }

    private void UpdateAutocomplete()
    {
        var text = CodeBox.Text;
        var caret = CodeBox.SelectionStart;

        var wordStart = caret;
        while (wordStart > 0 && (char.IsLetterOrDigit(text[wordStart - 1]) || text[wordStart - 1] == '_'))
        {
            wordStart--;
        }

        var word = text[wordStart..caret];
        if (word.Length < 2)
        {
            _autocompleteFlyout?.Hide();
            return;
        }

        var matches = AutocompleteWords
            .Where(w => w.StartsWith(word, StringComparison.OrdinalIgnoreCase) && !string.Equals(w, word, StringComparison.OrdinalIgnoreCase))
            .Take(20)
            .ToList();

        if (matches.Count == 0)
        {
            _autocompleteFlyout?.Hide();
            return;
        }

        _autocompleteWordStart = wordStart;

        _autocompleteListView ??= new ListView { SelectionMode = ListViewSelectionMode.Single, Width = 240, MaxHeight = 220 };
        _autocompleteListView.ItemClick -= AutocompleteList_ItemClick;
        _autocompleteListView.ItemClick += AutocompleteList_ItemClick;
        _autocompleteListView.IsItemClickEnabled = true;
        _autocompleteListView.ItemsSource = matches;
        _autocompleteListView.SelectedIndex = 0;

        _autocompleteFlyout ??= new Flyout { Placement = FlyoutPlacementMode.Bottom, ShouldConstrainToRootBounds = false };
        _autocompleteFlyout.Content = _autocompleteListView;
        _autocompleteFlyout.ShowAt(CodeBox);
    }

    private void AutocompleteList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is string word)
        {
            AcceptAutocomplete(word);
        }
    }

    private void AcceptAutocomplete(string word)
    {
        var caret = CodeBox.SelectionStart;
        var text = CodeBox.Text;
        var newText = text[.._autocompleteWordStart] + word + text[caret..];
        CodeBox.Text = newText;
        CodeBox.SelectionStart = _autocompleteWordStart + word.Length;
        CodeBox.SelectionLength = 0;
        _autocompleteFlyout?.Hide();
        CodeBox.Focus(FocusState.Programmatic);
    }

    // ----- Saved query list (left pane) -----

    private void RefreshSavedList()
    {
        var selectedId = (SavedQueriesList.SelectedItem as SavedSearch)?.Id;
        var queries = SavedSearchService.Load().Where(s => s.Kind == SavedSearchKind.Sql).ToList();
        SavedQueriesList.ItemsSource = queries;

        if (selectedId is { } id)
        {
            SavedQueriesList.SelectedItem = queries.FirstOrDefault(q => q.Id == id);
        }
    }

    private void SavedQueriesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SavedQueriesList.SelectedItem is SavedSearch search)
        {
            LoadQuery(search);
        }
    }

    private void LoadQuery(SavedSearch search)
    {
        _current = search;
        NameBox.Text = search.Name;
        CodeBox.Text = search.Sql ?? string.Empty;
        StatusText.Text = string.Empty;
    }

    private void NewQuery_Click(object sender, RoutedEventArgs e)
    {
        _current = null;
        SavedQueriesList.SelectedItem = null;
        NameBox.Text = "New Query";
        CodeBox.Text = "SELECT Path, Name, DirectoryPath, IsDirectory, SizeBytes, ModifiedTicks\nFROM Entries\nWHERE Name LIKE '%.zip'\nORDER BY SizeBytes DESC\n";
        StatusText.Text = string.Empty;
        ResultsGrid.Columns.Clear();
        ResultsGrid.ItemsSource = null;
        EmptyStatePanel.Visibility = Visibility.Visible;
    }

    /// The CommunityToolkit DataGrid's AutoGenerateColumns reflects over the bound item's own CLR
    /// properties - given a DataView/DataRowView (which isn't ICustomTypeDescriptor-aware here the
    /// way WPF's DataGrid is) that means its own Item/RowVersion/IsNew/... properties, not the SQL
    /// result's columns. So columns are built by hand from the DataTable schema, and rows are
    /// projected to Dictionary<string, object?> - WinUI's classic {Binding} supports the "[key]"
    /// indexer path syntax against IDictionary, which each DataGridTextColumn.Binding uses below.
    private void PopulateResultsGrid(DataTable table)
    {
        ResultsGrid.Columns.Clear();
        foreach (DataColumn column in table.Columns)
        {
            ResultsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = column.ColumnName,
                Binding = new Binding { Path = new PropertyPath($"[{column.ColumnName}]") },
            });
        }

        var rows = new List<Dictionary<string, object?>>(table.Rows.Count);
        foreach (DataRow row in table.Rows)
        {
            var values = new Dictionary<string, object?>(table.Columns.Count);
            foreach (DataColumn column in table.Columns)
            {
                var value = row[column];
                values[column.ColumnName] = value is DBNull ? null : value;
            }

            rows.Add(values);
        }

        ResultsGrid.ItemsSource = rows;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (_current is { } existing)
        {
            if (!string.Equals(existing.Name, name, StringComparison.Ordinal))
            {
                SavedSearchService.Rename(existing.Id, name);
            }

            SavedSearchService.UpdateSql(existing.Id, CodeBox.Text);
        }
        else
        {
            _current = SavedSearchService.Add(name, SavedSearchKind.Sql, rootPath: null, query: null, sql: CodeBox.Text);
        }

        RefreshSavedList();
        SavedQueriesList.SelectedItem = SavedQueriesList.Items.Cast<SavedSearch>().FirstOrDefault(s => s.Id == _current!.Id);
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _current = SavedSearchService.Add(name, SavedSearchKind.Sql, rootPath: null, query: null, sql: CodeBox.Text);
        RefreshSavedList();
        SavedQueriesList.SelectedItem = SavedQueriesList.Items.Cast<SavedSearch>().FirstOrDefault(s => s.Id == _current!.Id);
    }

    // ScriptManagerDialog-style inline rename/delete flyouts - AdvancedSearchDialog only ever runs
    // embedded in a ContentDialog, so a second ContentDialog here would throw.
    private void RenameQuery_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SavedSearch search } button)
        {
            return;
        }

        var nameBox = new TextBox { Text = search.Name, SelectionStart = 0, SelectionLength = search.Name.Length, Width = 220 };
        var confirmButton = new Button { Content = "Rename", HorizontalAlignment = HorizontalAlignment.Right };
        var flyout = new Flyout { Placement = FlyoutPlacementMode.Bottom };

        void Confirm()
        {
            var newName = nameBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(newName) && !string.Equals(newName, search.Name, StringComparison.Ordinal))
            {
                SavedSearchService.Rename(search.Id, newName);
                if (_current?.Id == search.Id)
                {
                    NameBox.Text = newName;
                }

                RefreshSavedList();
            }

            flyout.Hide();
        }

        confirmButton.Click += (_, _) => Confirm();
        nameBox.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Enter)
            {
                Confirm();
            }
        };

        flyout.Content = new StackPanel { Spacing = 8, Width = 240, Children = { nameBox, confirmButton } };
        flyout.ShowAt(button);
    }

    private void DeleteQuery_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SavedSearch search } button)
        {
            return;
        }

        var flyout = new Flyout { Placement = FlyoutPlacementMode.Bottom };
        var confirmButton = new Button { Content = "Delete", HorizontalAlignment = HorizontalAlignment.Right };
        confirmButton.Click += (_, _) =>
        {
            flyout.Hide();
            SavedSearchService.Remove(search);
            if (_current?.Id == search.Id)
            {
                NewQuery_Click(this, new RoutedEventArgs());
            }

            RefreshSavedList();
        };

        flyout.Content = new StackPanel
        {
            Spacing = 8,
            Width = 240,
            Children =
            {
                new TextBlock { Text = $"Delete the saved query \"{search.Name}\"? This can't be undone.", TextWrapping = TextWrapping.Wrap },
                confirmButton,
            },
        };
        flyout.ShowAt(button);
    }

    // ----- Run -----

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_runCts is not null)
        {
            _runCts.Cancel();
            return;
        }

        await RunQueryAsync();
    }

    private async Task RunQueryAsync()
    {
        var sql = CodeBox.Text;
        if (string.IsNullOrWhiteSpace(sql))
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _runCts = cts;
        RunButton.Content = "Cancel";
        RunningRing.IsActive = true;
        StatusText.Text = "Running...";

        try
        {
            var result = await AdvancedSearchQueryService.RunAsync(sql, cts.Token);
            PopulateResultsGrid(result.Table);
            EmptyStatePanel.Visibility = result.Table.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = $"{result.Table.Rows.Count:N0} row(s) in {result.Elapsed.TotalMilliseconds:N0} ms";

            if (_current is { } saved)
            {
                SavedSearchService.TouchLastRun(saved.Id);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            ResultsGrid.ItemsSource = null;
            EmptyStatePanel.Visibility = Visibility.Visible;
            StatusText.Text = string.Empty;
            ValidationText.Text = ex.Message;
            ValidationText.Visibility = Visibility.Visible;
        }
        finally
        {
            RunButton.Content = "Run";
            RunningRing.IsActive = false;
            _runCts = null;
        }
    }

    private void ResultsGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not Dictionary<string, object?> row ||
            !row.TryGetValue("Path", out var pathValue) ||
            pathValue?.ToString() is not { Length: > 0 } path)
        {
            return;
        }

        var isDirectory = row.TryGetValue("IsDirectory", out var isDirValue) && isDirValue is not null && Convert.ToInt64(isDirValue) != 0;

        if (isDirectory)
        {
            NavigateToResult?.Invoke(path, null);
        }
        else
        {
            var directoryPath = row.TryGetValue("DirectoryPath", out var dp) ? dp?.ToString() : null;
            NavigateToResult?.Invoke(directoryPath ?? System.IO.Path.GetDirectoryName(path) ?? path, path);
        }
    }
}
