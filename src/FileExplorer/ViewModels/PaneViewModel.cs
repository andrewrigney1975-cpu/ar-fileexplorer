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
    private string _searchText = string.Empty;
    private bool _isRecursiveSearch;
    private CancellationTokenSource? _searchCts;

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

    public bool CanNavigateUp => Directory.GetParent(CurrentPath) is not null;

    public RelayCommand NavigateUpCommand { get; }
    public RelayCommand NavigateBackCommand { get; }
    public RelayCommand NavigateForwardCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public void NavigateTo(string path, bool recordHistory = true)
    {
        if (!Directory.Exists(path))
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
        Load();
        RaiseNavCommands();
        PathChanged?.Invoke(this, EventArgs.Empty);
    }

    public void NavigateUp()
    {
        var parent = Directory.GetParent(CurrentPath);
        if (parent is not null)
        {
            NavigateTo(parent.FullName);
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
        Load();
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
        Load();
        RaiseNavCommands();
        PathChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Refresh(string? selectPathAfterLoad = null) => Load(selectPathAfterLoad);

    private void RaiseNavCommands()
    {
        NavigateUpCommand.RaiseCanExecuteChanged();
        NavigateBackCommand.RaiseCanExecuteChanged();
        NavigateForwardCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanNavigateUp));
    }

    private void Load(string? selectPathAfterLoad = null)
    {
        IsLoading = true;
        var path = CurrentPath;

        Task.Run(() => FileSystemService.GetItems(path)).ContinueWith(t =>
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (!string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase))
                {
                    return; // navigated away while loading
                }

                _allItems.Clear();
                _allItems.AddRange(t.Result);
                ApplyFilter();
                IsLoading = false;

                if (selectPathAfterLoad is not null)
                {
                    SelectedItem = Items.FirstOrDefault(i => string.Equals(i.FullPath, selectPathAfterLoad, StringComparison.OrdinalIgnoreCase));
                }
            });
        }, TaskScheduler.Default);
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
            source = source.Where(i => i.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in source)
        {
            Items.Add(item);
        }
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

        Items.Clear();
        foreach (var item in results)
        {
            Items.Add(item);
        }

        IsLoading = false;
    }
}
