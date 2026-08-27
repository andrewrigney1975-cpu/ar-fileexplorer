using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileExplorer.Models;
using FileExplorer.Services;
using Microsoft.UI.Dispatching;

namespace FileExplorer.ViewModels;

public sealed partial class PaneViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcher;
    private readonly IFileSystemService _fileSystemService;
    private readonly List<string> _back = new();
    private readonly List<string> _forward = new();
    private readonly List<FileSystemItem> _allItems = new();
    private CancellationTokenSource? _searchCts;

    // ApplyFilter() must NOT run while ClearSearchSilently() resets SearchText - the caller is
    // about to trigger its own reload/filter pass (navigation), and re-filtering here first would
    // be redundant and briefly show stale results.
    private bool _suppressSearchFilter;

    public PaneViewModel(DispatcherQueue dispatcher, IFileSystemService fileSystemService, string startPath)
    {
        _dispatcher = dispatcher;
        _fileSystemService = fileSystemService;
        CurrentPath = startPath;

        Refresh();
    }

    public ObservableCollection<FileSystemItem> Items { get; } = new();

    public event EventHandler? PathChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanNavigateUp))]
    [NotifyCanExecuteChangedFor(nameof(NavigateUpCommand))]
    public partial string CurrentPath { get; private set; }

    [ObservableProperty]
    public partial ViewMode ViewMode { get; set; } = ViewMode.Details;

    [ObservableProperty]
    public partial FileSystemItem? SelectedItem { get; set; }

    /// Full multi-selection snapshot, kept in sync by PaneView on SelectionChanged.
    public List<FileSystemItem> SelectedItems { get; set; } = new();

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// Set when a remote listing fails (session lost, permission denied, path doesn't exist,
    /// etc.) - CurrentPath/history are deliberately NOT updated in that case, since remote
    /// navigation skips the pre-check Directory.Exists gives local paths for free (see
    /// NavigateTo). Null whenever the last load succeeded.
    [ObservableProperty]
    public partial string? LoadError { get; private set; }

    /// Client-side filename filter applied to the already-loaded folder contents (or, when
    /// IsRecursiveSearch is on, a background filename+content scan of the whole subtree).
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    partial void OnSearchTextChanged(string value)
    {
        if (!_suppressSearchFilter)
        {
            ApplyFilter();
        }
    }

    /// When on, a non-empty SearchText searches the current folder's entire subtree (filename and,
    /// for text/code files, the first few KB of content) instead of just the loaded folder listing.
    [ObservableProperty]
    public partial bool IsRecursiveSearch { get; set; }

    partial void OnIsRecursiveSearchChanged(bool value) => ApplyFilter();

    /// Null until the user clicks a Details-view column header; once set, persists across
    /// reloads/searches until another column is clicked.
    [ObservableProperty]
    public partial SortColumn? ActiveSortColumn { get; private set; }

    [ObservableProperty]
    public partial bool SortAscending { get; private set; } = true;

    /// Clicking the same column again flips direction; clicking a different column selects it, ascending.
    public void ToggleSort(SortColumn column)
    {
        if (ActiveSortColumn == column)
        {
            SortAscending = !SortAscending;
        }
        else
        {
            ActiveSortColumn = column;
            SortAscending = true;
        }

        ApplyFilter();
    }

    public bool CanNavigateUp => RemotePathService.GetParent(CurrentPath) is not null;

    /// Feeds FolderVisitService, which MainWindow uses on startup to pre-warm the listing cache for
    /// whichever folders the user actually navigates into most - remote paths are excluded since
    /// pre-warming those would mean a network round-trip on every app launch for no guaranteed payoff.
    private static void RecordVisitIfLocal(string path)
    {
        if (!RemotePathService.IsRemote(path))
        {
            FolderVisitService.RecordVisit(path);
        }
    }

    public void NavigateTo(string path, bool recordHistory = true, string? selectPathAfterLoad = null)
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
        RecordVisitIfLocal(path);
        ClearSearchSilently();
        _ = LoadAsync(selectPathAfterLoad);
        RaiseNavCommands();
        PathChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanNavigateUp))]
    public void NavigateUp()
    {
        var parent = RemotePathService.GetParent(CurrentPath);
        if (parent is not null)
        {
            NavigateTo(parent);
        }
    }

    [RelayCommand(CanExecute = nameof(CanNavigateBack))]
    public void NavigateBack()
    {
        if (_back.Count == 0) return;
        var target = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        _forward.Add(CurrentPath);
        CurrentPath = target;
        RecordVisitIfLocal(target);
        ClearSearchSilently();
        _ = LoadAsync();
        RaiseNavCommands();
        PathChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanNavigateForward))]
    public void NavigateForward()
    {
        if (_forward.Count == 0) return;
        var target = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        _back.Add(CurrentPath);
        CurrentPath = target;
        RecordVisitIfLocal(target);
        ClearSearchSilently();
        _ = LoadAsync();
        RaiseNavCommands();
        PathChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool CanNavigateBack => _back.Count > 0;

    public bool CanNavigateForward => _forward.Count > 0;

    [RelayCommand]
    public void Refresh(string? selectPathAfterLoad = null) => _ = LoadAsync(selectPathAfterLoad, bypassCache: true);

    /// _back/_forward are plain Lists (not observable collections), so CanNavigateBack/Forward
    /// don't auto-notify - raised manually here alongside the command re-evaluation every
    /// navigation already needs to do.
    private void RaiseNavCommands()
    {
        NavigateBackCommand.NotifyCanExecuteChanged();
        NavigateForwardCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(CanNavigateForward));
    }

    /// Returns Task (not async void) so exceptions are observable and completion is awaitable, even
    /// though every current caller below still fires it with `_ = LoadAsync(...)` to match
    /// NavigateTo/NavigateUp/etc's existing synchronous-void signatures - those callers (button
    /// clicks, PathChanged handlers) aren't set up to await. WinUI's DispatcherQueueSynchronizationContext
    /// means the code after each await below still resumes on the UI thread automatically, same as
    /// it did through the explicit _dispatcher.TryEnqueue this replaced.
    private async Task LoadAsync(string? selectPathAfterLoad = null, bool bypassCache = false)
    {
        IsLoading = true;
        LoadError = null;
        var path = CurrentPath;

        List<FileSystemItem>? items = null;
        string? error = null;

        try
        {
            items = await _fileSystemService.GetItemsAsync(path, CancellationToken.None, bypassCache);
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
        if (SearchText.Length > 0)
        {
            _suppressSearchFilter = true;
            SearchText = string.Empty;
            _suppressSearchFilter = false;
        }
    }

    private void ApplyFilter()
    {
        _searchCts?.Cancel();

        if (IsRecursiveSearch && !string.IsNullOrWhiteSpace(SearchText))
        {
            var cts = new CancellationTokenSource();
            _searchCts = cts;
            _ = RunRecursiveSearchAsync(CurrentPath, SearchText, cts.Token);
            return;
        }

        Items.Clear();

        IEnumerable<FileSystemItem> source = _allItems;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            source = RankByFuzzyMatch(source, SearchText);
        }

        if (ActiveSortColumn is { } column)
        {
            source = ApplySort(source, column, SortAscending);
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
            if (FileExplorer.Helpers.FuzzyMatcher.TryScore(item.Name, query, out var score))
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
            results = await Task.Run(() => _fileSystemService.SearchRecursive(root, query, token), token);
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
        if (ActiveSortColumn is { } column)
        {
            ordered = ApplySort(ordered, column, SortAscending);
        }

        Items.Clear();
        foreach (var item in ordered)
        {
            Items.Add(item);
        }

        IsLoading = false;
    }
}
