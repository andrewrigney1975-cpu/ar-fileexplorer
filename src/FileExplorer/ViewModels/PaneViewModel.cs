using System.Collections.ObjectModel;
using FileExplorer.Helpers;
using FileExplorer.Models;
using FileExplorer.Services;
using Microsoft.UI.Dispatching;

namespace FileExplorer.ViewModels;

public sealed class PaneViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcher;
    private readonly List<string> _back = new();
    private readonly List<string> _forward = new();
    private readonly List<FileSystemItem> _allItems = new();

    private string _currentPath;
    private ViewMode _viewMode = ViewMode.Details;
    private FileSystemItem? _selectedItem;
    private bool _isActive;
    private bool _isLoading;
    private string? _loadError;
    private string _searchText = string.Empty;
    private bool _isRecursiveSearch;
    private CancellationTokenSource? _searchCts;
    private SortColumn? _sortColumn;
    private bool _sortAscending = true;

    public PaneViewModel(DispatcherQueue dispatcher, string startPath)
    {
        _dispatcher = dispatcher;
        _currentPath = startPath;

        NavigateUpCommand = new RelayCommand(() => NavigateUp(), () => CanNavigateUp);
        NavigateBackCommand = new RelayCommand(() => NavigateBack(), () => _back.Count > 0);
        NavigateForwardCommand = new RelayCommand(() => NavigateForward(), () => _forward.Count > 0);
        RefreshCommand = new RelayCommand(() => Refresh());

        Refresh();
    }

    public ObservableCollection<FileSystemItem> Items { get; } = new();

    public event EventHandler? PathChanged;

    public string CurrentPath
    {
        get => _currentPath;
        private set => SetProperty(ref _currentPath, value);
    }

    public ViewMode ViewMode
    {
        get => _viewMode;
        set => SetProperty(ref _viewMode, value);
    }

    public FileSystemItem? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }

    /// Full multi-selection snapshot, kept in sync by PaneView on SelectionChanged.
    public List<FileSystemItem> SelectedItems { get; set; } = new();

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    /// Set when a remote listing fails (session lost, permission denied, path doesn't exist,
    /// etc.) - CurrentPath/history are deliberately NOT updated in that case, since remote
    /// navigation skips the pre-check Directory.Exists gives local paths for free (see
    /// NavigateTo). Null whenever the last load succeeded.
    public string? LoadError
    {
        get => _loadError;
        private set => SetProperty(ref _loadError, value);
    }

    /// Client-side filename filter applied to the already-loaded folder contents (or, when
    /// IsRecursiveSearch is on, a background filename+content scan of the whole subtree).
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilter();
            }
        }
    }

    /// When on, a non-empty SearchText searches the current folder's entire subtree (filename and,
    /// for text/code files, the first few KB of content) instead of just the loaded folder listing.
    public bool IsRecursiveSearch
    {
        get => _isRecursiveSearch;
        set
        {
            if (SetProperty(ref _isRecursiveSearch, value))
            {
                ApplyFilter();
            }
        }
    }

    /// Null until the user clicks a Details-view column header; once set, persists across
    /// reloads/searches until another column is clicked.
    public SortColumn? ActiveSortColumn
    {
        get => _sortColumn;
        private set => SetProperty(ref _sortColumn, value);
    }

    public bool SortAscending
    {
        get => _sortAscending;
        private set => SetProperty(ref _sortAscending, value);
    }

    /// Clicking the same column again flips direction; clicking a different column selects it, ascending.
    public void ToggleSort(SortColumn column)
    {
        if (_sortColumn == column)
        {
            SortAscending = !_sortAscending;
        }
        else
        {
            ActiveSortColumn = column;
            SortAscending = true;
        }

        ApplyFilter();
    }

    public bool CanNavigateUp => RemotePathService.GetParent(CurrentPath) is not null;

    public RelayCommand NavigateUpCommand { get; }
    public RelayCommand NavigateBackCommand { get; }
    public RelayCommand NavigateForwardCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public void NavigateTo(string path, bool recordHistory = true)
    {
        // Remote existence isn't checked here (that would block this UI-thread call on a network
        // round-trip) - LoadAsync() below is the source of truth instead, surfacing a failure via
        // LoadError without touching CurrentPath/history if the remote listing fails.
        if (!RemotePathService.IsRemote(path) && !Directory.Exists(path))
        {
            return;
        }

        if (recordHistory && !string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            _back.Add(CurrentPath);
            _forward.Clear();
        }

        CurrentPath = path;
        ClearSearchSilently();
        _ = LoadAsync();
        RaiseNavCommands();
        PathChanged?.Invoke(this, EventArgs.Empty);
    }

    public void NavigateUp()
    {
        var parent = RemotePathService.GetParent(CurrentPath);
        if (parent is not null)
        {
            NavigateTo(parent);
        }
    }

    public void NavigateBack()
    {
        if (_back.Count == 0) return;
        var target = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        _forward.Add(CurrentPath);
        CurrentPath = target;
        ClearSearchSilently();
        _ = LoadAsync();
        RaiseNavCommands();
        PathChanged?.Invoke(this, EventArgs.Empty);
    }

    public void NavigateForward()
    {
        if (_forward.Count == 0) return;
        var target = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        _back.Add(CurrentPath);
        CurrentPath = target;
        ClearSearchSilently();
        _ = LoadAsync();
        RaiseNavCommands();
        PathChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Refresh(string? selectPathAfterLoad = null) => _ = LoadAsync(selectPathAfterLoad);

    private void RaiseNavCommands()
    {
        NavigateUpCommand.RaiseCanExecuteChanged();
        NavigateBackCommand.RaiseCanExecuteChanged();
        NavigateForwardCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanNavigateUp));
    }

    /// Returns Task (not async void) so exceptions are observable and completion is awaitable, even
    /// though every current caller below still fires it with `_ = LoadAsync(...)` to match
    /// NavigateTo/NavigateUp/etc's existing synchronous-void signatures - those callers (button
    /// clicks, PathChanged handlers) aren't set up to await. WinUI's DispatcherQueueSynchronizationContext
    /// means the code after each await below still resumes on the UI thread automatically, same as
    /// it did through the explicit _dispatcher.TryEnqueue this replaced.
    private async Task LoadAsync(string? selectPathAfterLoad = null)
    {
        IsLoading = true;
        LoadError = null;
        var path = CurrentPath;

        List<FileSystemItem>? items = null;
        string? error = null;

        try
        {
            items = await FileSystemService.GetItemsAsync(path, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error = ex.Message;
        }

        if (!string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            return; // navigated away while loading
        }

        IsLoading = false;

        if (error is not null)
        {
            LoadError = error;
            return;
        }

        _allItems.Clear();
        _allItems.AddRange(items!);
        ApplyFilter();

        if (selectPathAfterLoad is not null)
        {
            SelectedItem = Items.FirstOrDefault(i => string.Equals(i.FullPath, selectPathAfterLoad, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void ClearSearchSilently()
    {
        if (_searchText.Length > 0)
        {
            _searchText = string.Empty;
            OnPropertyChanged(nameof(SearchText));
        }
    }

    private void ApplyFilter()
    {
        _searchCts?.Cancel();

        if (_isRecursiveSearch && !string.IsNullOrWhiteSpace(_searchText))
        {
            var cts = new CancellationTokenSource();
            _searchCts = cts;
            _ = RunRecursiveSearchAsync(CurrentPath, _searchText, cts.Token);
            return;
        }

        Items.Clear();

        IEnumerable<FileSystemItem> source = _allItems;
        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            source = RankByFuzzyMatch(source, _searchText);
        }

        if (_sortColumn is { } column)
        {
            source = ApplySort(source, column, _sortAscending);
        }

        foreach (var item in source)
        {
            Items.Add(item);
        }
    }

    private static IEnumerable<FileSystemItem> RankByFuzzyMatch(IEnumerable<FileSystemItem> items, string query)
    {
        var scored = new List<(FileSystemItem Item, int Score)>();
        foreach (var item in items)
        {
            if (FuzzyMatcher.TryScore(item.Name, query, out var score))
            {
                scored.Add((item, score));
            }
        }
        return scored.OrderByDescending(x => x.Score).Select(x => x.Item);
    }

    /// Folders always group before files (matching Explorer); within each group, sorts by the
    /// chosen column and direction.
    private static IEnumerable<FileSystemItem> ApplySort(IEnumerable<FileSystemItem> items, SortColumn column, bool ascending)
    {
        var byGroup = items.OrderBy(i => !i.IsDirectory);

        return column switch
        {
            SortColumn.Modified => ascending ? byGroup.ThenBy(i => i.Modified) : byGroup.ThenByDescending(i => i.Modified),
            SortColumn.Kind => ascending ? byGroup.ThenBy(i => i.Kind, StringComparer.OrdinalIgnoreCase) : byGroup.ThenByDescending(i => i.Kind, StringComparer.OrdinalIgnoreCase),
            SortColumn.Size => ascending ? byGroup.ThenBy(i => i.SizeBytes) : byGroup.ThenByDescending(i => i.SizeBytes),
            _ => ascending ? byGroup.ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase) : byGroup.ThenByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase),
        };
    }

    private async Task RunRecursiveSearchAsync(string root, string query, CancellationToken token)
    {
        IsLoading = true;

        List<FileSystemItem> results;
        try
        {
            results = await Task.Run(() => FileSystemService.SearchRecursive(root, query, token), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        IEnumerable<FileSystemItem> ordered = results;
        if (_sortColumn is { } column)
        {
            ordered = ApplySort(ordered, column, _sortAscending);
        }

        Items.Clear();
        foreach (var item in ordered)
        {
            Items.Add(item);
        }

        IsLoading = false;
    }
}
