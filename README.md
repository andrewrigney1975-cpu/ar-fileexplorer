# Arexx Pro

A native dual-pane file explorer for Windows, built with WinUI 3 / Windows App SDK and .NET 8.

## Features

### Navigation & layout
- Left rail: drive list (with used-space bar + percentage per drive) and an expandable folder tree
- Dual-pane, **Workspace**-based browsing — each Workspace tab holds two independently-navigable panes side by side
- Workspaces can be renamed (right-click a tab → "Rename Workspace...", or double-click its header) and reordered by drag-and-drop
- Resizable left rail, pane splitter, and right-hand preview rail; preview pane width and the terminal drawer's open/closed state both persist across restarts
- Clickable breadcrumb path bar (click to edit as raw text, click a segment to jump to it)
- Back / forward / up navigation with history per pane
- Session restore: Workspaces (including custom names) and pane paths persist across restarts
- Command palette (`Ctrl+K`) for navigation, view switching, and running actions by name
- Collapsible left-rail sections (Saved Searches, Network Locations, Cloud Storage), VS Code style
- Custom title bar: app content (and its Mica backdrop) extends up into the OS caption area, with theme-matched caption buttons, so the window frame reads as one continuous surface

### Views
- Icons, List, Details, and Gallery (large-thumbnail) view modes per pane
- Details view has clickable, sortable column headers (Name / Date modified / Type / Size), folders always grouped before files
- Real image thumbnails in Icons/Gallery views

### File operations
- Drag-and-drop: same-drive drag moves, cross-drive drag copies, hold **Alt** to force a move
- Drag-to-tab: hover a drag over a background tab to switch to it before dropping
- Cut / Copy / Paste toolbar and context menu, with a shared clipboard between panes
- Rename (`F2`), "Move to folder..." (`F3`, creates a new folder from the current multi-selection)
- Delete to Recycle Bin (`Del`), permanent delete (`Shift+Del`)
- Batch rename with pattern-based multi-selection renaming
- Compress selection to `.zip` from the context menu; extract one or many selected `.zip` files at once, each to its own destination folder
- Undo for create-folder, rename, move, and copy operations
- Resilient, parallel, queued file-copy engine with automatic restart on transient I/O errors
- "Open with..." context menu entry

### Folder sync
- Right-click any folder → "Set sync source...", then right-click another folder (any pane, any Workspace) → "Set sync target"; both get an immediate highlight bar (orange for the source, green for the target) so the pairing is visible while you set it up
- Confirming a name in the resulting dialog saves the pairing as a named sync task; source/target folders keep their highlight bar afterward, wherever they're browsed
- A toolbar dropdown (next to File operations) lists the sync tasks whose source folder is visible in the current Workspace's Left or Right pane, with a trash icon (and confirmation) to delete a task; running a task enqueues it into the same File operations queue/list as any copy or move, with live progress
- One-way, copy-only: copies new/changed files from source → target; never deletes or touches files that exist only in the target
- A Windows notification reports success or failure (with the error) when a sync task finishes

### Scripting
- Command palette (`Ctrl+K`) → "Manage Scripts..." opens an in-app Script Manager: a list of saved scripts on the left, a code editor + Run/Save on the right, and an "API Reference..." button with the full function list
- Scripts are plain **JavaScript** (ES5.1, run by the embedded [Jint](https://github.com/sebastienros/jint) interpreter), each saved as a `.js` file; every saved script also gets its own `"Run Script: <name>"` entry in the command palette for one-key execution against the active pane's current selection
- API surface: `selection()` / `listFiles(path)` (folder contents), `currentPath`, `rename`/`copyTo`/`moveTo`/`deleteItem`/`createFolder`, `readText`/`writeText`/`exists`, `prompt`/`confirm` (blocking input dialogs), `notify` (Windows toast), `refresh` (reload open panes), and `log` (shown in the run's output)
- Scripts run off the UI thread with a 30-second timeout guard; `deleteItem` defaults to the Recycle Bin; script-driven file changes are **not** tracked by Undo, and there's no sandboxing beyond the timeout (the app already ships a full terminal, so a script has no more reach than the user already does)

### Search & organization
- Per-folder filename search with typo-tolerant fuzzy matching (e.g. `rdme` matches `readme.txt`)
- Recursive search toggle: scans the current folder's subtree by filename and, for text/code files, file content
- Saved/smart searches: pin a (root path, query) pair to the left rail and re-run it with one click
- Color tags/labels on files and folders, shown as a colored dot in every view mode
- Duplicate file finder (size-then-hash scan) with a review-and-delete dialog
- SHA-256 checksum command with copy-to-clipboard

### Preview pane
- Text and code file preview (first few KB); programming source files (`.cs`, `.js`/`.ts`, `.py`, `.java`, `.c`/`.cpp`, `.go`, `.rs`, `.json`, `.css`, `.xml`/`.xaml`/`.html`) get a line-number gutter and color-coded syntax highlighting (keywords/strings/comments/numbers), following the app's light/dark theme
- Image preview
- Video preview (native `MediaPlayerElement`)
- PDF preview (via an embedded WebView2, using its built-in PDF viewer)
- Text-only preview for modern Office documents (`.docx` / `.xlsx` / `.pptx`) via OpenXML extraction
- Spacebar quick-look popup using the same preview pane

### Storage integrations
- LAN/network shares: pin `\\server\share` UNC locations to the left rail; mapped network drives get a distinct icon
- Cloud storage: auto-detects OneDrive, Google Drive, Dropbox, and Box **local sync folders** and pins them to the left rail, with online-only/always-available status badges on files inside them. This reads what each provider's desktop client already mirrors locally — it does **not** use any provider's web API, so no accounts, OAuth, or credentials are involved.

### Other
- Built-in terminal drawer, opens at the active pane's folder
- Right-click context menu (via `ContextRequested`, covers mouse, keyboard, and touch)
- Custom hi-res application icon
- Windows toast notifications (e.g. sync task completion/failure)

## Toolchain

| | |
|---|---|
| UI framework | WinUI 3 (Windows App SDK 1.6) |
| Runtime | .NET 8, Windows-only (`net8.0-windows10.0.19041.0`) |
| Language | C# 12 (`LangVersion=latest`), nullable reference types enabled |
| Packaging | Unpackaged (`WindowsPackageType=None`), self-contained, single-file `.exe` — no MSIX/store dependency |
| Architecture | x64 only |
| Min OS | Windows 10 1809 (build 17763) |
| Pattern | Hand-rolled MVVM (`ObservableObject` / `RelayCommand` in `Helpers/`) — no external MVVM library |

### NuGet dependencies

| Package | Used for |
|---|---|
| `Microsoft.WindowsAppSDK` | WinUI 3 controls and runtime |
| `Microsoft.Windows.SDK.BuildTools` | PRI/manifest build tooling |
| `Microsoft.Web.WebView2` | PDF preview (renders via WebView2's built-in PDF viewer) |
| `DocumentFormat.OpenXml` | Text-only preview extraction for `.docx`/`.xlsx`/`.pptx` |
| `Jint` | Embedded JavaScript interpreter for user scripts (pure C#, no native deps) |

Everything else — Recycle Bin delete, zip compress/extract, SHA-256 hashing, file-system watching, drag-and-drop, syntax-highlighted previews, Windows toast notifications — uses only the .NET/Windows App SDK base class libraries (e.g. `Microsoft.VisualBasic.FileIO.FileSystem` for Recycle Bin operations, `System.IO.Compression` for zip, `Microsoft.Windows.AppNotifications` — bundled in `Microsoft.WindowsAppSDK`, no extra package — for toasts).

**Runtime prerequisite:** PDF preview requires the WebView2 runtime, which ships with Windows 11 and current Windows 10 by default. On an older or locked-down Windows 10 install without it, PDF preview falls back to a generic file icon instead of crashing.

**Note:** toast notifications use `AppNotificationManager`, which is best-supported for packaged (MSIX) apps. This app is unpackaged (no `Package.appxmanifest`) but toast registration/activation has been confirmed working from this unpackaged exe on this dev machine; `NotificationService` still wraps every call defensively so a platform quirk on another machine/OS build can't crash or block the app.

## Build process

### Prerequisites
- Visual Studio 2022 (or later) with the **.NET Desktop Development** and **Windows App SDK** workloads, which provide the WinUI 3 project templates, PRI (Package Resource Index) generation, and Appx manifest MSBuild tasks this project depends on
- .NET 8 SDK

### Why build via Visual Studio's MSBuild, not `dotnet build`

This project must be built with Visual Studio's own `MSBuild.exe`, not a bare `dotnet build`. A plain `dotnet build` is missing the PRI/Appx MSBuild tasks that WinUI 3 projects need to generate resources correctly (`ms-appx:///...` resource URIs will fail to resolve at runtime otherwise). Locate VS's MSBuild and invoke it directly, for example:

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" `
    "src\FileExplorer\FileExplorer.csproj" `
    /p:Configuration=Debug /p:Platform=x64
```

The exact path depends on your VS edition, version, and install drive — e.g. on this project's dev machine it's `F:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe`. Adjust accordingly.

### Building from the IDE

Open `FileExplorer.sln` in Visual Studio, select the `x64` platform and `Debug`/`Release` configuration, and build/run (`F5`) as normal — this uses the same MSBuild tasks under the hood.

### Output

The build produces a self-contained `FileExplorer.exe` (with the Windows App SDK runtime bundled in) at:

```
src\FileExplorer\bin\x64\<Configuration>\net8.0-windows10.0.19041.0\win-x64\FileExplorer.exe
```

No installer or MSIX packaging step is required — the exe runs directly.

## Project structure

```
src/FileExplorer/
  Models/        Data records (FileSystemItem, FolderNode, TabState, SavedSearch, SyncRole, ...)
  ViewModels/    MainViewModel, PaneViewModel, TabViewModel, enums (ViewMode, SortColumn)
  Views/         PaneView, PreviewPane, TerminalPane, ScriptManagerDialog (XAML + code-behind)
  Services/      File system access, search, tagging, undo, clipboard, cloud/network
                 detection, duplicate finder, Office text extraction, session/layout
                 persistence, folder sync (SyncTaskService), toast notifications,
                 user scripting (ScriptService, ScriptEngineService)
  Converters/    XAML value converters
  Helpers/       ObservableObject/RelayCommand (hand-rolled MVVM base), FuzzyMatcher,
                 SyntaxHighlighter (preview-pane code coloring)
  Assets/        App icon
```

Per-user application data (tags, saved searches, network locations, session state, window
layout, sync tasks) is stored as JSON under `%LocalAppData%\FileExplorerApp\`; saved scripts are
plain `.js` files under `%LocalAppData%\FileExplorerApp\Scripts\`.
