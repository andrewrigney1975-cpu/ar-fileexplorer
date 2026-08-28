using CommunityToolkit.Mvvm.ComponentModel;
using FileExplorer.Services;
using Microsoft.UI.Dispatching;

namespace FileExplorer.ViewModels;

public sealed partial class TabViewModel : ObservableObject
{
    /// Segoe Fluent Icons PUA codepoints: the tab strip's default folder glyph and Home's house glyph.
    public const string DefaultGlyph = "";
    public const string HomeGlyph = "";

    private bool _hasCustomHeader;
    private PaneViewModel _activePane;
    private readonly IRemoteConnectionService _remoteConnectionService;

    public TabViewModel(DispatcherQueue dispatcher, IFileSystemService fileSystemService, IRemoteConnectionService remoteConnectionService, string startPath, string? name = null)
        : this(dispatcher, fileSystemService, remoteConnectionService, startPath, startPath, name)
    {
    }

    public TabViewModel(DispatcherQueue dispatcher, IFileSystemService fileSystemService, IRemoteConnectionService remoteConnectionService, string leftPath, string rightPath, string? name = null, bool isHome = false)
    {
        _remoteConnectionService = remoteConnectionService;
        IsHome = isHome;
        LeftPane = new PaneViewModel(dispatcher, fileSystemService, leftPath);
        RightPane = new PaneViewModel(dispatcher, fileSystemService, rightPath);

        // Home's left pane is the drive picker, not a browsable folder list, so its right pane is
        // the one global/keyboard actions should target.
        _activePane = isHome ? RightPane : LeftPane;
        LeftPane.IsActive = !isHome;
        RightPane.IsActive = isHome;

        LeftPane.PathChanged += (_, _) => UpdateHeader();
        RightPane.PathChanged += (_, _) => UpdateHeader();

        IconGlyph = isHome ? HomeGlyph : DefaultGlyph;

        if (isHome)
        {
            Header = "Home";
        }
        else if (string.IsNullOrWhiteSpace(name))
        {
            UpdateHeader();
        }
        else
        {
            Rename(name);
        }
    }

    /// The fixed, non-closable "Home" workspace (drive picker + system drive). Its locations are
    /// not persisted and reset every launch, so it never appears in the saved session.
    public bool IsHome { get; }

    public PaneViewModel LeftPane { get; }
    public PaneViewModel RightPane { get; }

    // Left as a hand-written property (rather than [ObservableProperty]) because the constructor
    // deliberately assigns the backing field directly (`_activePane = LeftPane`) to set the initial
    // active pane without firing this setter's side effects before PathChanged handlers are wired up.
    public PaneViewModel ActivePane
    {
        get => _activePane;
        set
        {
            if (SetProperty(ref _activePane, value))
            {
                LeftPane.IsActive = ReferenceEquals(value, LeftPane);
                RightPane.IsActive = ReferenceEquals(value, RightPane);
                UpdateHeader();
            }
        }
    }

    [ObservableProperty]
    public partial string Header { get; private set; } = "New Workspace";

    /// Segoe Fluent Icons glyph shown on the tab. Fixed to the house glyph for Home; user-settable
    /// (and session-persisted) for every other workspace via the tab's "Set Icon..." context action.
    [ObservableProperty]
    public partial string IconGlyph { get; set; } = DefaultGlyph;

    /// True once the user has picked a non-default icon - only these are written to the session.
    public bool HasCustomIcon => !IsHome && IconGlyph != DefaultGlyph;

    /// Applies a user-chosen tab glyph (no-op for Home); null/blank reverts to the default folder glyph.
    public void SetIcon(string? glyph)
    {
        if (IsHome)
        {
            return;
        }

        IconGlyph = string.IsNullOrWhiteSpace(glyph) ? DefaultGlyph : glyph;
    }

    /// True once the user has explicitly renamed this workspace, so path changes no longer overwrite the header.
    public bool HasCustomHeader => _hasCustomHeader;

    /// Sets a user-chosen name for this workspace, or (given null/blank) reverts to the auto-generated name.
    public void Rename(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            _hasCustomHeader = false;
            UpdateHeader();
            return;
        }

        _hasCustomHeader = true;
        Header = name;
    }

    public void RefreshBoth()
    {
        LeftPane.Refresh();
        RightPane.Refresh();
    }

    private void UpdateHeader()
    {
        if (IsHome || _hasCustomHeader)
        {
            return;
        }

        var path = ActivePane.CurrentPath;
        string? name;

        if (RemotePathService.IsRemote(path))
        {
            name = RemotePathService.GetFileName(path);
            if (string.IsNullOrEmpty(name) && RemotePathService.TryParse(path, out _, out var connectionId, out _))
            {
                // At a connection's root - fall back to its saved display name rather than "".
                name = _remoteConnectionService.Find(connectionId)?.Name;
            }
        }
        else
        {
            name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        }

        Header = string.IsNullOrEmpty(path) ? "New Workspace" : (name is { Length: > 0 } n ? n : path);
    }
}
