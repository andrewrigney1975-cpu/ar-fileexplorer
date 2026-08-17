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

    private string _currentPath;
    private ViewMode _viewMode = ViewMode.Details;
    private FileSystemItem? _selectedItem;
    private bool _isActive;
    private bool _isLoading;

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
        Load();
        RaiseNavCommands();
        PathChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Refresh() => Load();

    private void RaiseNavCommands()
    {
        NavigateUpCommand.RaiseCanExecuteChanged();
        NavigateBackCommand.RaiseCanExecuteChanged();
        NavigateForwardCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanNavigateUp));
    }

    private void Load()
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

                Items.Clear();
                foreach (var item in t.Result)
                {
                    Items.Add(item);
                }
                IsLoading = false;
            });
        }, TaskScheduler.Default);
    }
}
