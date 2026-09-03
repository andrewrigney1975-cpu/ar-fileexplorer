# Docket

A native dual-pane file explorer for Windows, built with WinUI 3 / Windows App SDK and .NET 8.

## Features

### Navigation & layout
- Left rail: drive list (with used-space bar + percentage per drive) and an expandable folder tree; right-click a drive → "Open in new workspace" opens it as the left pane of a brand-new Workspace tab, right pane defaulting to the user's home directory; "Analyse Disk" / "Benchmark Disk" jump straight into the Disk Space Analyser / Disk Benchmark (below) with that drive pre-selected, skipping their drive-picker screen
- Dual-pane, **Workspace**-based browsing — each Workspace tab holds two independently-navigable panes side by side
- Workspaces can be renamed (right-click a tab → "Rename Workspace...", or double-click its header) and reordered by drag-and-drop
- Resizable left rail, pane splitter, and right-hand preview rail; the preview pane's width and open/closed state, and the terminal drawer's open/closed state, all persist across restarts
- Clickable breadcrumb path bar (click to edit as raw text, click a segment to jump to it)
- Back / forward / up navigation with history per pane
- Session restore: Workspaces (including custom names) and pane paths persist across restarts
- Switching Workspace tabs auto-refreshes both panes of the tab being switched to, so disk changes made while it wasn't the active tab are reflected immediately rather than waiting for a manual refresh
- The active tab's panes also auto-refresh whenever the app window regains focus (Alt-Tab back to it, click it after using another program), for the same reason - picks up files added/changed/removed elsewhere while the app was in the background
- Folder listings are cached in memory for 5 minutes, so revisiting a folder (back/forward, re-navigating into it) is instant instead of re-scanning the disk; a folder with an active folder watch (see Automation, below) is invalidated the moment a new file lands in it, and the toolbar Refresh button (or focus-regain auto-refresh above) always bypasses the cache for a live re-scan. Turn off from Control Centre → Preferences
- Command palette (`Ctrl+K`) for navigation, view switching, running actions by name, and opening the Control Centre; a persistent "Command Palette (Ctrl+K)" bar sits top-center of the toolbar as a discoverable, clickable entry point to the same popup
- Collapsible left-rail sections (Favourites, Saved Searches, Network Locations, Cloud Storage), VS Code style
- Favourites: pin any folder from the left rail's own `+` button or a folder's context menu ("Add to Favourites"), click to navigate, `−` to unpin
- Custom title bar: app content (and its Mica backdrop) extends up into the OS caption area, with theme-matched caption buttons, so the window frame reads as one continuous surface

### Views
- Icons, List, Details, and Gallery (large-thumbnail) view modes per pane
- Details view has clickable, sortable column headers (Name / Date modified / Type / Size), folders always grouped before files, plus an Attributes column showing Windows Explorer-style letter codes (R/H/S/A/C/E/L/T/O/I/P — see [file attribute constants](https://learn.microsoft.com/en-us/windows/win32/fileio/file-attribute-constants))
- Type-ahead-to-jump, identical across all four view modes: click into a pane's file list and start typing to select and scroll to the first item whose name starts with what you've typed. Typing different characters within a second accumulates a prefix search from the top of the list; repeating the same character instead cycles forward through every match one at a time (mashing "s" steps through each S-item in turn)
- Drag-rectangle (marquee) multi-select: click-drag over empty space to rubber-band select everything the rectangle touches
- Real image thumbnails in Icons/Gallery views, rendered at a configurable bitmap size (Control Centre → Thumbnails, default 192px — 2× the Gallery tile size, so they don't look upscaled/blurry at that default) and cached both in memory and to a hidden per-folder file (`.docket-thumbs.cache`) so revisiting a folder — or relaunching the app entirely — shows them instantly instead of re-decoding; an edited image's cache entry is invalidated by its last-write-time, and changing the configured size invalidates every folder's cache lazily (each regenerates at the new size next time it's opened, rather than an upfront rescan)
- Folders get a thumbnail too: the first image found inside them (recursing into subfolders up to 3 levels deep if the folder has none directly, capped so a huge image-less tree can't stall browsing) is used as a mini-preview instead of the plain folder icon, with a small folder-glyph badge overlaid in the corner so it's still clearly a folder
- `.avif` thumbnails and preview: Windows' image codec (WIC) can't decode AVIF without a separate OS extension, so AVIF files are decoded via an embedded [libheif](https://github.com/strukturag/libheif) instead (`AvifImageService`), transparently alongside every other image format

### File operations
- Drag-and-drop: same-drive drag moves, cross-drive drag copies, hold **Alt** to force a move
- Drag-to-tab: hover a drag over a background tab to switch to it before dropping
- Cut / Copy / Paste toolbar and context menu, with a shared clipboard between panes
- Rename (`F2`), "Move to folder..." (`F3`, creates a new folder from the current multi-selection)
- Delete to Recycle Bin (`Del`), permanent delete (`Shift+Del`); a failed delete (locked file, permissions, or an unsynced cloud-only file) shows a dialog naming the item and why — including a specific hint when the folder is under a detected OneDrive/Google Drive/Dropbox/Box root — instead of silently doing nothing
- Batch rename with pattern-based multi-selection renaming (`{name}` / `{n}` / `{n:000}`), or switch the same dialog to Find & Replace (Regex) mode, or Random GUID mode (renames every selected item to a new random GUID, extension preserved) — a live preview shows the result (or a parse error) for each mode as you type
- Symbolic links and junctions: right-click empty space → "New link..." to create either (junctions need only a folder target and, unlike symbolic links, never need Developer Mode or admin rights); linked items get a small link badge in every view mode, and Properties/the Type column shows "Symbolic Link" or "Junction". Copy/move and folder sync never descend into a link — the link itself gets recreated at the destination pointing at the same target, rather than duplicating (or, for a self-referential link, infinitely recursing into) whatever it points to
- Right-click empty space → "Export folder listing (JSON)..." to write the current pane's Details-view listing (name, full path, is-directory, kind, size in bytes and display form, modified timestamp, attributes) to a JSON file saved into that same folder, named after the folder's own path plus an export timestamp; hidden for remote (FTP/SFTP) locations
- Compress selection to `.zip` from the context menu; extract one or many selected archives at once (`.zip`/`.rar`/`.7z`/`.tar`/`.gz`/`.tgz`/`.bz2`/`.xz`, auto-detected by content via [SharpCompress](https://github.com/adamhathcock/sharpcompress)), each to its own destination folder
- "Convert To..." (context menu, for any mix of image files and folders): pick a target image format (PNG / JPEG / WebP / BMP / GIF / TIFF / TGA / QOI), a quality for the lossy ones, whether folders contribute their direct children only or every image in the subtree, and a per-file post-action — keep the original, recycle it, or move it into an "Originals" subfolder. Each file is converted and has its post-action applied before the next one starts; a progress dialog can be stopped mid-run, and a summary lists conversions, skips (already in the target format) and any failures. Pure-managed via [ImageSharp](https://github.com/SixLabors/ImageSharp); AVIF/HEIC/HEIF sources decode through the existing libheif path first
- Properties dialog (context menu → "Properties"): type, location, size, created/modified/accessed timestamps, and editable Read-only/Hidden attributes for a single item; aggregate file/folder counts and combined size for a multi-selection, with folder sizes computed recursively in the background
- Filename collisions on copy/move (drag-and-drop, paste, "Move to folder...") and sync tasks prompt **Overwrite / Skip / Rename / Cancel**, with an "apply to all remaining conflicts" option; Rename auto-appends a bracketed number (`file (2).txt`) with no further input needed
- Undo for create-folder, rename, move, and copy operations
- Resilient, parallel, queued file-copy engine with automatic restart on transient I/O errors
- The File operations toolbar icon spins while any job is queued or running, so activity is visible without opening the flyout — also spins for a running script, whether triggered manually, by a folder watch, or by an interval schedule
- "Open with..." context menu entry

### Folder sync
- Right-click any folder → "Set sync source...", then right-click another folder (any pane, any Workspace) → "Set sync target"; both get an immediate highlight bar (orange for the source, green for the target) so the pairing is visible while you set it up
- Confirming a name in the resulting dialog saves the pairing as a named sync task; source/target folders keep their highlight bar afterward, wherever they're browsed. An "Include hidden/system files" checkbox in that same dialog (off by default) controls whether hidden/system files and folders on the source side are mirrored at all — off skips them entirely (they're never even walked, so a hidden folder's contents don't count either), matching the usual expectation that a folder sync means "my visible files," not OS/app metadata like `Thumbs.db` or `desktop.ini`
- A toolbar dropdown (next to File operations) lists the sync tasks whose source folder is visible in the current Workspace's Left or Right pane, with a trash icon (and confirmation) to delete a task; running a task enqueues it into the same File operations queue/list as any copy or move, with live progress
- One-way, copy-only: copies new/changed files from source → target; never deletes or touches files that exist only in the target. When a file differs at the same relative path in both, it's a collision handled the same way as any copy/move (Overwrite/Skip/Rename/Cancel, with "apply to all")
- A Windows notification reports success or failure (with the error) when a sync task finishes

### Scripting
- Control Centre → Scripts: a list of saved scripts on the left, a code editor (with a line-number gutter, synced to the editor's scroll position) + Run/Save on the right, and an "API Reference..." button with the full function list; each list entry has a Rename button alongside Delete — renaming actually renames the underlying file (not a copy) and repoints any folder watch or interval schedule bound to that script at the new name, so automation keeps working across the rename
- Scripts are plain **JavaScript** (ES5.1, run by the embedded [Jint](https://github.com/sebastienros/jint) interpreter), each saved as a `.js` file; every saved script also gets its own `"Run Script: <name>"` entry in the command palette for one-key execution against the active pane's current selection
- API surface: `selection()` / `listFiles(path)` (folder contents), `currentPath`, `addedFiles` (the files that triggered a folder-watch run; empty otherwise), `rename`/`copyTo`/`moveTo`/`deleteItem`/`createFolder`, `readText`/`writeText`/`exists`, `prompt`/`confirm` (blocking input dialogs), `notify` (Windows toast), `refresh` (reload open panes), and `log` (shown in the run's output)
- Scripts run off the UI thread with a 30-second timeout guard; `deleteItem` defaults to the Recycle Bin; script-driven file changes are **not** tracked by Undo, and there's no sandboxing beyond the timeout (the app already ships a full terminal, so a script has no more reach than the user already does)
- Can be turned off entirely from Control Centre → Preferences: hides the Scripts/Automation-watch UI, context-menu entries, and command palette entries, without deleting any saved script

### Automation: folder watches & schedules
- Right-click any folder → "Watch this folder..." to bind it to a saved script; the folder gets a light-blue highlight bar (independent of, and stackable with, the sync source/target bars) wherever it's browsed, and "Stop watching folder" removes the binding
- Watching is backed by a live `FileSystemWatcher` per folder; rapid bursts of new files (e.g. a batch copy landing at once) are debounced (~750ms) into a single script run, with the newly-added files passed in as `addedFiles`
- A watched folder's own watcher is paused for the duration of its triggered script's run, so a script that renames or moves files in place within the watched folder can't have its own writes re-trigger the same watch and loop forever
- Control Centre → Automation lists all folder watches (with delete), plus interval-based schedules: run a saved script, or trigger a saved sync task, every N minutes — reusing the same script engine and sync/File-operations queue as manual runs
- A background poller (30s tick) checks due schedules independently of whether Control Centre is open; both watch triggers and schedule runs report completion via a Windows toast, same as a manually-run script
- Folder watching can be turned off separately from scripting in Control Centre → Preferences (hides the watch context-menu entries and highlight bar; existing watch bindings are kept, just inactive, and schedules are unaffected)

### Control Centre
- Command palette (`Ctrl+K`) → "Control Centre..." opens a single dialog with a left-hand section list: **Scripts**, **Sync Tasks**, **Automation**, **Thumbnails**, **Search Index**, **Preferences**, **Keyboard Shortcuts**, **About**
- Sync Tasks: every saved sync task (not filtered to the current pane, unlike the toolbar dropdown), each with "Run now", Rename, and delete — sync tasks are referenced elsewhere (schedules) by an internal ID rather than name, so renaming is a pure display-name change and never breaks a scheduled run; an "Include hidden/system files" checkbox under each task's paths toggles that task's setting at any time, not just at creation
- Thumbnails: the bitmap size thumbnails/folder previews are generated and cached at (see Views, above)
- Preferences: seven feature toggles — **PowerShell terminal**, **Sync Tasks**, **Folder watching**, **Scripting**, **Folder listing cache**, **Search Everywhere index** (each defaulting to on), and **Web Browse (LAN media server)** (defaulting to **off**) — switching one off hides its toolbar button(s), context-menu entries, command palette entries, and pane highlight bars, without deleting anything already saved (scripts, sync tasks, watches, schedules, and the search index all stay intact and reappear when re-enabled); Folder listing cache and Search Everywhere index instead just stop caching/indexing and hide their own section/command-palette entry, since neither has a toolbar button to hide
- About: app name and version
- Scripts, Sync Tasks, and Search Index are themselves hidden from the section list when their Preferences toggle is off (re-enable from Preferences, which is always shown, to bring them back)

### Search & organization
- Per-folder filename search with typo-tolerant fuzzy matching (e.g. `rdme` matches `readme.txt`)
- Recursive search toggle: scans the current folder's subtree by filename and, for text/code files, file content
- Saved/smart searches: pin a (root path, query) pair to the left rail and re-run it with one click
- Color tags/labels on files and folders, shown as a colored dot in every view mode
- Duplicate file finder (size-then-hash scan) with a review-and-delete dialog; deletion runs off the UI thread with a live "Deleting N of M..." progress dialog, so a large batch (e.g. cleaning up a slow removable drive) never looks like a hang. Also reachable from the right-click menu — on a folder (scoped to that folder) or on empty pane space (scoped to the pane's current folder) — not just F4/command palette
- Checksum command (context menu → "Checksum..."): SHA-256, SHA-1, or MD5, with copy-to-clipboard; paste a known hash to verify a file against it (per-file MATCH/NO MATCH), or select exactly two files with no expected hash entered to get an automatic "identical"/"differ" comparison
- Search Everywhere (`F9`, or the command palette): instant substring search across a persistent, local SQLite index of whichever folders/drives you've added under Control Centre → Search Index — separate from the per-pane recursive search above, which always walks the live filesystem instead. Indexing is opt-in per folder (nothing is scanned until you add one — no "index everything" default) and stays current via a recursive `FileSystemWatcher` per indexed root plus a full rescan every 24h as a backstop. Deliberately not built on the USN journal (would need admin rights, which this unpackaged app never requests) or the OS's own Windows Search indexer (only covers the user profile/Libraries by default, so a data drive would silently return nothing). Results are ranked with the same typo-tolerant `FuzzyMatcher` the per-pane search uses; double-click or Enter opens the result in a brand-new "Search Results" Workspace tab (rather than navigating whatever pane was last active), so existing workspaces are never disturbed. Control Centre → Search Index shows each indexed location's own entry count (with its own re-index button, for refreshing just one location), the index's total size on disk, and a progress ring with a live-updating count while a scan is running. A scan commits every 2,000 entries rather than holding one transaction open for the whole run, and every filesystem call carries a watchdog timeout (skip-and-continue, logged) - plain blocking Win32 APIs have no cancellation support, so an unresponsive drive (spun down, a failing USB/SATA bridge) could otherwise stall the entire scan forever with no way to recover short of restarting the app. Every SQLite connection sets a 10s `busy_timeout` so a scan's writes and the watcher's debounced writes don't fail each other outright on contention; the Search Index panel also polls once a second while it's the visible section (diffed against what's already shown, so it doesn't flicker), rather than depending solely on a change event to know a scan finished
- Browser Integration (Control Centre → Search Index): registers this exe as a Chrome/Edge [Native Messaging](https://developer.chrome.com/docs/apps/nativeMessaging/) host, scoped to one extension ID, so a browser extension can ask "does a folder named X already exist?" (exact name match, folders only, read-only) against the Search Everywhere index. Deliberately not a local HTTP/WebSocket server, which would open a port any webpage's script could probe — Native Messaging never opens a network port at all; the browser launches the host directly over stdio, and only the registered extension's origin is permitted to invoke it. The host runs as this same exe launched with the calling extension's origin as a command-line argument (that's how it's detected — Chrome's manifest format has no field for custom launch arguments), so it needs no separate binary. Firefox uses a different manifest format and registry hive and isn't supported yet

### Preview pane
- Text and code file preview (first few KB); programming source files (`.cs`, `.js`/`.ts`, `.py`, `.java`, `.c`/`.cpp`, `.go`, `.rs`, `.json`, `.css`, `.xml`/`.xaml`/`.html`) get a line-number gutter and color-coded syntax highlighting (keywords/strings/comments/numbers), following the app's light/dark theme
- Image preview, with a technical "Image Info" panel underneath: native pixel dimensions, format, bit depth, and color model (RGB/RGBA/Grayscale/YUV, derived from the decoder's pixel format, or libheif's reported bit depth for AVIF), plus whichever EXIF fields the file actually has (camera make/model/lens, date taken, exposure time, f-number, focal length, ISO, orientation, flash, GPS) read via the Windows Property System — the same source Explorer's own Details tab uses. Loads asynchronously after the image itself so metadata never delays the visible preview; not available for AVIF (libheif's embedded metadata block isn't parsed)
- Geotagged photos get explicit "GPS latitude"/"GPS longitude"/"GPS altitude" rows in the EXIF list, plus a small embedded map underneath plotting the coordinate on OpenStreetMap tiles via [Leaflet](https://leafletjs.com/) (both free, no API key/license fee) rendered in the same WebView2 used for PDF preview. If the file also recorded a camera heading (`GPS.ImgDirection`), a red marker rotated to that bearing is drawn alongside the location pin — as a field-of-view wedge (apex pinned at the pin, two rays spread ±half the estimated FoV, no base line) when the shot's 35mm-equivalent focal length (`System.Photo.FocalLengthInFilm`) lets the horizontal FoV be estimated, or a plain direction arrow otherwise. (The canonical `System.GPS.Latitude`/`Longitude` properties silently come back empty on some codecs even when every other GPS property resolves — worked around by falling back to reading the same EXIF GPS tags via WIC's raw metadata query paths.)
- Video preview (native `MediaPlayerElement`)
- PDF preview (via an embedded WebView2, using its built-in PDF viewer)
- Text-only preview for modern Office documents (`.docx` / `.xlsx` / `.pptx`) via OpenXML extraction
- Spacebar quick-look popup using the same preview pane

### Storage integrations
- LAN/network shares: pin `\\server\share` UNC locations to the left rail for one-click browsing (no drive letter involved), or actually map one to a drive letter via the Network Locations section's link-icon button ("Map Network Drive...") — picks an unused letter, optional different credentials, optional "Reconnect at sign-in"; mapped drives show up in the regular drive list with a distinct network icon and can be disconnected from the same dialog
- Cloud storage: auto-detects OneDrive, Google Drive, Dropbox, and Box **local sync folders** and pins them to the left rail, with online-only/always-available status badges on files inside them. This reads what each provider's desktop client already mirrors locally — it does **not** use any provider's web API, so no accounts, OAuth, or credentials are involved.
- FTP, FTPS (explicit), and SFTP: a "Remote Connections" left-rail section (its own "+" button) saves connection profiles (name, protocol, host, port, username — **passwords are never saved to disk**, prompted fresh each time you connect and kept in memory only for that session); clicking a saved connection browses it as a first-class location in the normal dual-pane view, with a working breadcrumb, double-click navigation into subfolders, rename/delete/new folder, and Checksum (which reads the remote file as a stream, no local temp file). Upload and download run through the same File Operations queue/progress UI as any local copy, in either direction — drag-and-drop or Cut/Copy/Paste between a remote pane and a local one both work, including the usual Overwrite/Skip/Rename/Cancel collision dialog (an Overwrite against a remote destination is explicitly called out as permanent, since there's no remote Recycle Bin). SFTP host keys are pinned trust-on-first-use (a later mismatch is a hard failure, never silently re-accepted) via [SSH.NET](https://github.com/sshnet/SSH.NET); FTP/FTPS via [FluentFTP](https://github.com/robinrodricks/FluentFTP). **Not supported (deliberate v1 scope, not oversights):** transferring directly between two remote connections (download to a local folder first, then upload from there); Undo for anything touching a remote location; thumbnails, symbolic link/junction creation, folder watch, folder sync tasks, colour tags, and the Properties dialog for remote items (all local-only, hidden from a remote item's context menu); and restoring a remote pane location across app restarts (falls back to that pane's local default).

### Web browsing (LAN media server)
Right-click a folder → **"Web Browse From Here..."** starts a small embedded HTTP server that serves *that one folder tree* so you can browse its media from a phone, tablet, or another PC on the same network. It renders a directory page (navigable subfolders plus a thumbnail grid with sizes), a click-to-open lightbox with arrow-key navigation (a Quick Look analogue), and a "Show folder as slideshow" page that mirrors the native slideshow — big image, arrow/PageUp-Down/Home/End keys, a thumbnail strip that scrolls to track position. `/file` responses honour HTTP `Range`, so videos seek. Thumbnails reuse the app's own on-disk thumbnail cache.

This is the deliberate, tightly-guarded exception to the "no local HTTP server" stance behind [Browser Integration](#search--organization) above — it only ships with **all** of these:

| Guard | How |
| --- | --- |
| **Opt-in** | Off by default — enable **Web Browse (LAN media server)** in Control Centre → Preferences; the menu item is hidden until you do, and the server only runs while a session is open |
| **Unguessable token** | Every generated link carries a 128-bit key (`?k=…`); any request without it gets `403`, so a random page probing the port can't browse anything (static CSS/JS are the only unauthenticated routes — they carry no data) |
| **Root scoping** | Every request path is resolved and rejected if it escapes the folder you picked — no access to the rest of the disk |
| **Read-only** | `GET` only — no upload, write, rename, or delete endpoints |
| **Auto-stop** | Stops on app exit, after 30 minutes idle, when you turn the toggle off, or with **Stop server** in the control dialog |
| **Bind & transport** | Binds `0.0.0.0` (LAN-reachable by design, per the feature's intent); Windows Firewall prompts on first use. Plain HTTP, no TLS — meant for a trusted home/office network |

Built on a hand-rolled `TcpListener` HTTP/1.1 layer rather than `HttpListener`, so it needs no one-time administrator URL-ACL registration. `MediaWebServer` and its inlined HTML/CSS/JS (`WebAssets`, no CDN) carry zero WinUI dependency and are covered by integration tests (token, `Range`, path traversal, method rejection, directory/slideshow rendering).

### Disk tools
- Disk Space Analyser (toolbar pie-chart icon): opens a large dialog showing every drive in an icon grid with its free/total space; double-click a drive to see a donut chart plus a size-descending list of what's consuming space at that level (each folder's size is a recursive total of everything inside it). Double-click a chart slice or list folder row to drill into it; the smallest items beyond the top 12 are grouped into a single "Other" wedge so the chart stays readable. Hovering a chart slice shows a floating popover with that item's kind, size, attributes, created/modified dates, and owner. A "Home"-rooted breadcrumb tracks navigation and jumps back to the drive grid, and an "Export listing (JSON)..." button writes the current level's item details to a JSON file in that same folder.
- Disk Benchmark (toolbar heart-pulse icon, beside the Analyser): pick a drive from the same drive grid, then it runs a CrystalDiskMark-style sequential/random read/write test — 4 MB, 64 MB, and 1 GB test files, each read and written both sequentially and at random 4 KB offsets (12 results total) — plotted live as a column chart, in MB/s, one column per result, as each finishes. Uses unbuffered disk I/O (`FILE_FLAG_NO_BUFFERING`) so results reflect real drive speed rather than Windows' file cache; a result that had to fall back to ordinary buffered I/O (rare — happens if a drive rejects that flag) is marked with a red asterisk and a tooltip explaining why, since a buffered/cached number isn't a trustworthy measurement. The test file for the system drive is written to the user's temp folder rather than the drive root, since the root of the system drive normally isn't writable without elevation (still the same physical drive, so the result is unaffected). The right-hand panel shows drive info from WMI: manufacturer, model, capacity, filesystem, and interface type/approximate speed (PCIe lane count isn't shown — not exposed by any standard WMI class). "Export results (JSON)" saves to the Documents folder once the run finishes.
- Disk Activity Monitor (toolbar icon, beside the Benchmark): every ready drive gets its own row — label on the left, a live read/write line chart (green = read, orange = write, in MB/s) on the right — sampled 4 times a second via WMI's `Win32_PerfFormattedData_PerfDisk_LogicalDisk` counters, with a rolling 60-second history per drive. Row heights are computed to divide the dialog's height evenly across however many drives are present, so a typical system shows every drive without a scrollbar. Pooled/dynamic volumes (e.g. StableBit DrivePool) that WMI reports under an internal `HarddiskVolumeN` name rather than a drive letter don't have a counterpart perf-counter instance keyed by that drive letter, so those rows stay at zero — a known limitation of the underlying WMI class, not a bug in the polling.
- A collapsible "Disk Activity" section in the left rail, below Remote Connections, shows the same read/write line chart aggregated (summed) across every drive, updating 4 times a second the app is running — a glance-able "something is happening" indicator, deliberately with no numeric readout (the per-drive breakdown with numbers is what the Disk Activity Monitor dialog above is for).

### Other
- Built-in terminal drawer, opens at the active pane's folder; can be turned off from Control Centre → Preferences (hides its toolbar toggle and closes it if open)
- Right-click context menu (via `ContextRequested`, covers mouse, keyboard, and touch)
- Custom hi-res application icon
- Windows toast notifications (e.g. sync task completion/failure)
- App version shown in Control Centre → About, as `major.minor.build` (e.g. `1.00.037`) — Major/Minor are hand-set MSBuild properties in the `.csproj`; Build auto-increments on every build via an MSBuild target (`BuildNumber.txt`, machine-local/gitignored)

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
| `LibHeifSharp` + `LibHeif.Native.win-x64` | AVIF thumbnail/preview decoding via libheif — the only dependency here with a native (non-.NET) component |
| `SharpCompress` | Multi-format archive extraction (RAR/7z/TAR/GZ/BZip2/XZ, in addition to .zip) — pure C#, no native deps |
| `SSH.NET` | SFTP client (`Renci.SshNet`) — remote connections, transfers, and SSH host-key verification |
| `FluentFTP` | FTP/FTPS client — remote connections and transfers |
| `System.Management` | WMI queries for the Disk Benchmark's drive hardware info panel |
| `Microsoft.Data.Sqlite` | Local persistent index backing Search Everywhere (native SQLite binary per-RID, like `LibHeif.Native`) |

Everything else — Recycle Bin delete, zip compress/extract, SHA-256 hashing, file-system watching, drag-and-drop, syntax-highlighted previews, Windows toast notifications, the Disk Space Analyser's donut chart (hand-drawn with plain `Path`/`ArcSegment` geometry — a SkiaSharp-based charting library was tried first but its WinUI rendering depends on ANGLE/`SwapChainPanel`, which has a known unresolved bug for unpackaged apps and rendered blank) — uses only the .NET/Windows App SDK base class libraries (e.g. `Microsoft.VisualBasic.FileIO.FileSystem` for Recycle Bin operations, `System.IO.Compression` for zip, `Microsoft.Windows.AppNotifications` — bundled in `Microsoft.WindowsAppSDK`, no extra package — for toasts). Symbolic link creation/reading is native .NET (`Directory`/`File.CreateSymbolicLink`, `.LinkTarget`); telling a symbolic link apart from a junction needs one small direct `kernel32.dll` P/Invoke (`FindFirstFileW`, reading the reparse tag `.NET` doesn't expose), and creating a junction shells out to the OS's own `mklink /J` rather than hand-rolling the reparse-point buffer layout. Network drive mapping is a `mpr.dll` P/Invoke (`WNetAddConnection2W`/`WNetCancelConnection2W`) — the same API `net use` and Explorer's own "Map Network Drive" wizard use; hashing supports SHA-256/SHA-1/MD5, all from `System.Security.Cryptography`.

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

The build produces a self-contained `docket.exe` (with the Windows App SDK runtime bundled in) at:

```
src\FileExplorer\bin\<Configuration>\net8.0-windows10.0.19041.0\win-x64\docket.exe
```

No installer or MSIX packaging step is required — the exe runs directly.

### MSIX packaging (Microsoft Store)

`src/FileExplorer.Package/` is a separate Windows Application Packaging Project (`.wapproj`)
that wraps the exe above into an MSIX for Store submission, without changing the plain-exe
distribution at all — see [`src/FileExplorer.Package/README.md`](src/FileExplorer.Package/README.md)
for the full build/signing/sideload-testing walkthrough and what's still needed on Partner
Center's side (account, app-name reservation, Store listing) before it can actually be
submitted.

## Testing

Two xUnit projects under `tests/`, split by whether the code under test needs WinUI/Windows App SDK types:

- **`tests/FileExplorer.Tests`** — pure logic with zero WinUI dependency (`RemotePathService`, `FileOperationService`, `JsonFileStore<T>`, `FuzzyMatcher`, `LoggingService`, `MediaWebServer` + `WebAssets`, `ImageConversionService`). Links the real source files from `src/FileExplorer` rather than duplicating them. Builds and runs with plain `dotnet test` from the project directory — no Visual Studio involved.
- **`tests/FileExplorer.WinUI.Tests`** — code that needs a real `FileSystemItem` or other WinUI-touching type (`FileSystemItem`'s own formatting/display logic, `RenamePatternService`). References `FileExplorer.csproj` directly via `ProjectReference`. Because of that reference, this project **cannot be built** with the dotnet CLI — same `MrtCore.PriGen` limitation that keeps the main app from building via `dotnet build` (see "Why build via Visual Studio's MSBuild" above). Build it with VS's MSBuild exactly like the app:

  ```powershell
  & "F:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" `
      "tests\FileExplorer.WinUI.Tests\FileExplorer.WinUI.Tests.csproj" `
      /p:Configuration=Debug /v:minimal /nr:false /m:1
  ```

  Once built, running the tests works fine through the ordinary dotnet CLI (only the *build* step touches the Windows App SDK targets) — from the project directory, run `dotnet test` in its skip-rebuild mode (see `dotnet test --help`) against the Debug configuration.

## Project structure

```
src/FileExplorer/
  Models/        Data records (FileSystemItem, FolderNode, TabState, SavedSearch, SyncRole,
                 RemoteConnection, RemoteProtocol, ...)
  ViewModels/    MainViewModel, PaneViewModel, TabViewModel, enums (ViewMode, SortColumn)
  Views/         PaneView, PreviewPane, TerminalPane, ScriptManagerDialog, AutomationDialog,
                 ControlCentreDialog, PropertiesDialog (XAML + code-behind)
  Services/      File system access, search, tagging, undo, clipboard, cloud/network
                 detection, duplicate finder, Office text extraction, session/layout
                 persistence, folder sync (SyncTaskService), toast notifications,
                 user scripting (ScriptService, ScriptEngineService), folder-watch
                 triggers (WatchService) and interval schedules (ScheduleService),
                 thumbnail caching/generation (ThumbnailCacheService), image metadata/EXIF
                 reading (ImageMetadataService), AVIF decoding (AvifImageService),
                 collision prompts (FileCollisionService), Favourites (FavouriteService),
                 user preferences/feature toggles (SettingsService), symbolic link/junction
                 detection and creation (ReparsePointService), network drive mapping
                 (NetworkDriveService), FTP/FTPS/SFTP remote connections
                 (RemoteConnectionService, RemoteHostKeyStore, RemoteSessionManager,
                 RemotePathService, IRemoteFileSystem + SftpFileSystem/FtpFileSystem adapters)
  Converters/    XAML value converters
  Helpers/       ObservableObject/RelayCommand (hand-rolled MVVM base), FuzzyMatcher,
                 SyntaxHighlighter (preview-pane code coloring), AppVersionInfo
                 (reads the build-stamped version for Control Centre's About tab)
  Assets/        App icon
```

Per-user application data (tags, saved searches, network locations, session state, window
layout, sync tasks, folder watches, schedules, preferences) is stored as JSON under
`%LocalAppData%\FileExplorerApp\`; saved scripts are plain `.js` files under
`%LocalAppData%\FileExplorerApp\Scripts\`. The one exception is the
thumbnail cache, which lives as a hidden `.docket-thumbs.cache` file inside each folder it caches
(not centrally), so it travels with that folder if it's moved or copied elsewhere.
