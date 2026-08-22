using System.Collections.ObjectModel;
using FileExplorer.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace FileExplorer.Services;

/// Serializes drag-and-drop file operations into a background queue: same-drive moves are instant
/// renames, everything else streams through the resilient, parallel copy engine.
public sealed class FileOperationQueueService
{
    private const int MaxParallelFiles = 4;

    private readonly DispatcherQueue _dispatcher;
    private readonly Func<XamlRoot> _getXamlRoot;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Queue<FileOperationJob> _pending = new();
    private readonly object _lock = new();

    public FileOperationQueueService(DispatcherQueue dispatcher, Func<XamlRoot> getXamlRoot)
    {
        _dispatcher = dispatcher;
        _getXamlRoot = getXamlRoot;
        _ = Task.Run(ProcessLoopAsync);
        Current = this;
    }

    public static FileOperationQueueService? Current { get; private set; }

    public ObservableCollection<FileOperationJob> Jobs { get; } = new();

    public event EventHandler<FileOperationJob>? JobCompleted;

    public FileOperationJob Enqueue(
        IReadOnlyList<string> sourcePaths,
        string destinationFolder,
        FileDropOperation kind,
        bool destinationWasCreatedForThisJob = false)
    {
        var job = new FileOperationJob
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            SourcePaths = sourcePaths,
            DestinationFolder = destinationFolder,
            DestinationWasCreatedForThisJob = destinationWasCreatedForThisJob,
        };

        _dispatcher.TryEnqueue(() => Jobs.Insert(0, job));

        lock (_lock)
        {
            _pending.Enqueue(job);
        }

        _signal.Release();
        return job;
    }

    public FileOperationJob EnqueueSync(SyncTaskState task)
    {
        var job = new FileOperationJob
        {
            Id = Guid.NewGuid(),
            Kind = FileDropOperation.Sync,
            SourcePaths = new[] { task.SourcePath },
            DestinationFolder = task.TargetPath,
            SyncTaskName = task.Name,
            IncludeHiddenSystemFiles = task.IncludeHiddenSystemFiles,
        };

        _dispatcher.TryEnqueue(() => Jobs.Insert(0, job));

        lock (_lock)
        {
            _pending.Enqueue(job);
        }

        _signal.Release();
        return job;
    }

    public void Cancel(FileOperationJob job) => job.CancellationTokenSource.Cancel();

    private async Task ProcessLoopAsync()
    {
        while (true)
        {
            await _signal.WaitAsync().ConfigureAwait(false);

            FileOperationJob? job;
            lock (_lock)
            {
                job = _pending.Count > 0 ? _pending.Dequeue() : null;
            }

            if (job is null)
            {
                continue;
            }

            await RunJobAsync(job).ConfigureAwait(false);
        }
    }

    private async Task RunJobAsync(FileOperationJob job)
    {
        _dispatcher.TryEnqueue(() => job.Status = FileOperationStatus.Running);
        var token = job.CancellationTokenSource.Token;

        try
        {
            if (job.Kind == FileDropOperation.Sync)
            {
                await RunFolderSyncAsync(job, token).ConfigureAwait(false);

                _dispatcher.TryEnqueue(() =>
                {
                    job.ProgressPercent = 100;
                    job.Status = FileOperationStatus.Completed;
                });
                return;
            }

            var sourceIsRemote = job.SourcePaths.Count > 0 && RemotePathService.IsRemote(job.SourcePaths[0]);
            var destIsRemote = RemotePathService.IsRemote(job.DestinationFolder);

            if (sourceIsRemote || destIsRemote)
            {
                if (sourceIsRemote && destIsRemote)
                {
                    throw new NotSupportedException(
                        "Copying directly between two remote connections isn't supported yet - download to a local folder first, then upload from there.");
                }

                await RunRemoteAwareJobAsync(job, sourceIsRemote, token).ConfigureAwait(false);

                _dispatcher.TryEnqueue(() =>
                {
                    job.ProgressPercent = 100;
                    job.Status = FileOperationStatus.Completed;
                });
                return;
            }

            var resolved = await ResolveTopLevelCollisionsAsync(job.SourcePaths, job.DestinationFolder, token).ConfigureAwait(false);
            var allSameDrive = job.SourcePaths.All(p => FileOperationService.SameDrive(p, job.DestinationFolder));

            if (job.Kind == FileDropOperation.Move && allSameDrive)
            {
                // Same-volume move is an atomic filesystem rename - already optimal, no streaming needed.
                var moved = new List<(string Source, string Destination)>();
                foreach (var (source, destination) in resolved)
                {
                    token.ThrowIfCancellationRequested();
                    if (MoveByRename(source, destination))
                    {
                        moved.Add((source, destination));
                    }
                }

                if (moved.Count > 0)
                {
                    UndoService.Instance.Push(new MoveUndo(moved, job.DestinationWasCreatedForThisJob ? job.DestinationFolder : null));
                }
            }
            else
            {
                var plan = BuildCopyPlan(resolved);
                _dispatcher.TryEnqueue(() => job.BytesTotal = plan.TotalBytes);

                foreach (var dir in plan.DirectoriesToCreate)
                {
                    Directory.CreateDirectory(dir);
                }

                // Best-effort: a link that fails to recreate (e.g. a dangling target, or a
                // symbolic link needing Developer Mode/elevation this process doesn't have)
                // shouldn't fail the whole copy over one item.
                foreach (var (linkSource, linkDestination) in plan.Links)
                {
                    token.ThrowIfCancellationRequested();
                    await ReparsePointService.RecreateLinkAsync(linkSource, linkDestination).ConfigureAwait(false);
                }

                long bytesDoneTotal = 0;
                var progressLock = new object();

                await Parallel.ForEachAsync(
                    plan.Files,
                    new ParallelOptions { MaxDegreeOfParallelism = MaxParallelFiles, CancellationToken = token },
                    async (file, ct) =>
                    {
                        _dispatcher.TryEnqueue(() => job.CurrentFileName = Path.GetFileName(file.Source));

                        await ResilientFileCopy.CopyFileAsync(
                            file.Source,
                            file.Destination,
                            delta =>
                            {
                                long done;
                                lock (progressLock)
                                {
                                    bytesDoneTotal += delta;
                                    done = bytesDoneTotal;
                                }

                                _dispatcher.TryEnqueue(() =>
                                {
                                    job.BytesDone = done;
                                    job.ProgressPercent = job.BytesTotal > 0 ? Math.Min(100, done * 100.0 / job.BytesTotal) : 100;
                                });
                            },
                            ct).ConfigureAwait(false);
                    }).ConfigureAwait(false);

                if (job.Kind == FileDropOperation.Move)
                {
                    // Only delete sources that were actually copied - a Skip'd collision must leave
                    // its original untouched rather than vanishing without ever reaching the destination.
                    foreach (var (source, _) in plan.TopLevel)
                    {
                        DeleteSource(source);
                    }

                    UndoService.Instance.Push(new MoveUndo(plan.TopLevel, job.DestinationWasCreatedForThisJob ? job.DestinationFolder : null));
                }
                else
                {
                    UndoService.Instance.Push(new CopyUndo(plan.TopLevel.Select(p => p.Destination).ToList()));
                }
            }

            _dispatcher.TryEnqueue(() =>
            {
                job.ProgressPercent = 100;
                job.Status = FileOperationStatus.Completed;
            });
        }
        catch (OperationCanceledException)
        {
            _dispatcher.TryEnqueue(() => job.Status = FileOperationStatus.Canceled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // job.ErrorMessage isn't bound anywhere in the Operations flyout XAML (only Status is) -
            // without this the user sees just "Failed" with no reason why.
            LoggingService.LogWarning($"FileOperationQueueService.RunJobAsync: {job.Title}", ex);
            _dispatcher.TryEnqueue(() =>
            {
                job.ErrorMessage = ex.Message;
                job.Status = FileOperationStatus.Failed;
            });
        }
        catch (Exception ex)
        {
            // Any other unexpected failure must still end this one job rather than escape - an
            // unhandled exception here would otherwise kill ProcessLoopAsync's while(true) forever,
            // silently freezing every future file operation for the rest of the app session.
            LoggingService.LogError($"FileOperationQueueService.RunJobAsync (unexpected): {job.Title}", ex);
            _dispatcher.TryEnqueue(() =>
            {
                job.ErrorMessage = ex.Message;
                job.Status = FileOperationStatus.Failed;
            });
        }
        finally
        {
            _dispatcher.TryEnqueue(() => JobCompleted?.Invoke(this, job));
        }
    }

    private sealed record RemoteCopyPlan(
        List<(string Source, string Destination, long Size)> Files,
        List<string> LocalDirectoriesToCreate,
        List<string> RemoteDirectoriesToCreate,
        long TotalBytes);

    /// Handles a job where exactly one side (source or destination) is remote - the plain
    /// local<->local path above (BuildCopyPlan/ResolveTopLevelCollisionsAsync/ResilientFileCopy)
    /// is untouched and still handles that case entirely on its own; remote<->remote is rejected
    /// by the caller before this ever runs. Deliberately single-threaded, unlike the
    /// MaxParallelFiles-parallel local copy loop above: neither SftpClient nor AsyncFtpClient is
    /// safe for concurrent operations against one connection.
    private async Task RunRemoteAwareJobAsync(FileOperationJob job, bool sourceIsRemote, CancellationToken token)
    {
        var plan = await BuildRemoteAwareCopyPlanAsync(job.SourcePaths, job.DestinationFolder, sourceIsRemote, token).ConfigureAwait(false);
        _dispatcher.TryEnqueue(() => job.BytesTotal = plan.TotalBytes);

        foreach (var dir in plan.LocalDirectoriesToCreate)
        {
            Directory.CreateDirectory(dir);
        }

        foreach (var remoteDir in plan.RemoteDirectoriesToCreate)
        {
            token.ThrowIfCancellationRequested();

            if (!RemotePathService.TryParse(remoteDir, out _, out var connectionId, out var bareRemoteDir))
            {
                continue;
            }

            var dirSession = RemoteSessionManager.TryGetSession(connectionId);
            if (dirSession is null)
            {
                continue;
            }

            if (!await dirSession.ExistsAsync(bareRemoteDir, token).ConfigureAwait(false))
            {
                await dirSession.CreateDirectoryAsync(bareRemoteDir, token).ConfigureAwait(false);
            }
        }

        long bytesDoneBeforeCurrentFile = 0;

        foreach (var (source, destination, size) in plan.Files)
        {
            token.ThrowIfCancellationRequested();

            var fileName = RemotePathService.IsRemote(source) ? RemotePathService.GetFileName(source) : Path.GetFileName(source);
            _dispatcher.TryEnqueue(() => job.CurrentFileName = fileName);

            var baselineForThisFile = bytesDoneBeforeCurrentFile;
            var progress = new Progress<long>(bytesForThisFile =>
            {
                var done = baselineForThisFile + bytesForThisFile;
                _dispatcher.TryEnqueue(() =>
                {
                    job.BytesDone = done;
                    job.ProgressPercent = job.BytesTotal > 0 ? Math.Min(100, done * 100.0 / job.BytesTotal) : 100;
                });
            });

            await TransferOneFileAsync(source, destination, sourceIsRemote, progress, token).ConfigureAwait(false);
            bytesDoneBeforeCurrentFile += size;
        }

        // No Undo support for anything touching a remote location (see UndoActions.cs - every
        // action there is a direct local Directory/File call with no remote equivalent).
        if (job.Kind == FileDropOperation.Move)
        {
            foreach (var source in job.SourcePaths)
            {
                token.ThrowIfCancellationRequested();
                await DeleteRemoteOrLocalDestinationAsync(source, token).ConfigureAwait(false);
            }
        }
    }

    private static string CombineAcrossSchemes(string basePath, string name) =>
        RemotePathService.IsRemote(basePath) ? RemotePathService.Combine(basePath, name) : Path.Combine(basePath, name);

    private async Task<bool> DestinationExistsAsync(string path, CancellationToken token)
    {
        if (!RemotePathService.IsRemote(path))
        {
            return File.Exists(path) || Directory.Exists(path);
        }

        if (!RemotePathService.TryParse(path, out _, out var connectionId, out var remotePath))
        {
            return false;
        }

        var session = RemoteSessionManager.TryGetSession(connectionId);
        return session is not null && await session.ExistsAsync(remotePath, token).ConfigureAwait(false);
    }

    /// No Recycle Bin equivalent exists remotely, so an Overwrite collision against a remote
    /// destination (or a remote source being deleted after a Move) is always permanent there,
    /// unlike the local DeleteToRecycleBin case.
    private async Task DeleteRemoteOrLocalDestinationAsync(string path, CancellationToken token)
    {
        if (!RemotePathService.IsRemote(path))
        {
            DeleteToRecycleBin(path);
            return;
        }

        if (!RemotePathService.TryParse(path, out _, out var connectionId, out var remotePath))
        {
            return;
        }

        var session = RemoteSessionManager.TryGetSession(connectionId);
        if (session is null)
        {
            return;
        }

        var info = await session.GetInfoAsync(remotePath, token).ConfigureAwait(false);
        if (info.IsDirectory)
        {
            await DeleteRemoteDirectoryRecursiveAsync(session, remotePath, token).ConfigureAwait(false);
        }
        else
        {
            await session.DeleteFileAsync(remotePath, token).ConfigureAwait(false);
        }
    }

    /// SftpClient.DeleteDirectoryAsync only removes an already-empty directory (unlike
    /// FluentFTP's DeleteDirectory, which recurses on its own) - emptying it first here makes
    /// this safe for both protocols rather than relying on protocol-specific behavior. Public so
    /// PaneView's own remote-delete context menu action can reuse it instead of duplicating this.
    public static async Task DeleteRemoteDirectoryRecursiveAsync(IRemoteFileSystem session, string remoteDirPath, CancellationToken token)
    {
        foreach (var entry in await session.ListAsync(remoteDirPath, token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();

            var childPath = remoteDirPath.TrimEnd('/') + "/" + entry.Name;
            if (entry.IsDirectory)
            {
                await DeleteRemoteDirectoryRecursiveAsync(session, childPath, token).ConfigureAwait(false);
            }
            else
            {
                await session.DeleteFileAsync(childPath, token).ConfigureAwait(false);
            }
        }

        await session.DeleteDirectoryAsync(remoteDirPath, token).ConfigureAwait(false);
    }

    private static async Task TransferOneFileAsync(string source, string destination, bool sourceIsRemote, IProgress<long> progress, CancellationToken token)
    {
        if (sourceIsRemote)
        {
            if (!RemotePathService.TryParse(source, out _, out var connectionId, out var remotePath))
            {
                throw new IOException($"Invalid remote source path: {source}");
            }

            var session = RemoteSessionManager.TryGetSession(connectionId)
                ?? throw new IOException("Not connected - reconnect from the Remote Connections list.");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await session.DownloadAsync(remotePath, destination, progress, token).ConfigureAwait(false);
        }
        else
        {
            if (!RemotePathService.TryParse(destination, out _, out var connectionId, out var remotePath))
            {
                throw new IOException($"Invalid remote destination path: {destination}");
            }

            var session = RemoteSessionManager.TryGetSession(connectionId)
                ?? throw new IOException("Not connected - reconnect from the Remote Connections list.");

            await session.UploadAsync(source, remotePath, progress, token).ConfigureAwait(false);
        }
    }

    /// Async plan-builder for the local<->remote case, standing in for BuildCopyPlan above (which
    /// stays purely local/synchronous). Collision handling only checks each top-level item
    /// (matching BuildCopyPlan's own scope - nested collisions inside a folder aren't prompted for
    /// either), and skips the "apply to all" convenience the local path offers since that would
    /// need extra state threaded through a recursive, mixed local/remote walk for comparatively
    /// little benefit at v1.
    private async Task<RemoteCopyPlan> BuildRemoteAwareCopyPlanAsync(
        IReadOnlyList<string> sourcePaths, string destinationFolder, bool sourceIsRemote, CancellationToken token)
    {
        var files = new List<(string Source, string Destination, long Size)>();
        var localDirs = new List<string>();
        var remoteDirs = new List<string>();

        foreach (var source in sourcePaths)
        {
            token.ThrowIfCancellationRequested();

            var name = sourceIsRemote ? RemotePathService.GetFileName(source) : Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar));
            var destination = CombineAcrossSchemes(destinationFolder, name);

            if (await DestinationExistsAsync(destination, token).ConfigureAwait(false))
            {
                var resolution = await FileCollisionService.ResolveAsync(name, destination, allowApplyToAll: false, _dispatcher, _getXamlRoot).ConfigureAwait(false);

                switch (resolution.Action)
                {
                    case CollisionAction.Cancel:
                        throw new OperationCanceledException();
                    case CollisionAction.Skip:
                        continue;
                    case CollisionAction.Rename:
                        var renamed = string.IsNullOrWhiteSpace(resolution.RenameTo) ? name : Path.GetFileName(resolution.RenameTo);
                        destination = CombineAcrossSchemes(destinationFolder, renamed);
                        break;
                    case CollisionAction.Overwrite:
                        await DeleteRemoteOrLocalDestinationAsync(destination, token).ConfigureAwait(false);
                        break;
                }
            }

            if (sourceIsRemote)
            {
                if (!RemotePathService.TryParse(source, out _, out var connectionId, out var remotePath))
                {
                    continue;
                }

                var session = RemoteSessionManager.TryGetSession(connectionId)
                    ?? throw new IOException("Not connected - reconnect from the Remote Connections list.");

                var info = await session.GetInfoAsync(remotePath, token).ConfigureAwait(false);

                if (info.IsDirectory)
                {
                    localDirs.Add(destination);
                    await CollectRemoteSourceTreeAsync(session, source, destination, files, localDirs, token).ConfigureAwait(false);
                }
                else
                {
                    files.Add((source, destination, info.Size));
                }
            }
            else
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(source);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LoggingService.LogWarning($"FileOperationQueueService.BuildRemoteAwareCopyPlanAsync (skipped): {source}", ex);
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    // Symlinks/junctions are skipped rather than uploaded-as-their-target - matches
                    // the local copy engine's own rule (CollectCopyEntries), and neither FTP nor
                    // SFTP has a link concept this app exposes an equivalent for anyway.
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    remoteDirs.Add(destination);
                    await CollectLocalSourceTreeAsync(source, destination, files, remoteDirs, token).ConfigureAwait(false);
                }
                else
                {
                    files.Add((source, destination, new FileInfo(source).Length));
                }
            }
        }

        return new RemoteCopyPlan(files, localDirs, remoteDirs, files.Sum(f => f.Size));
    }

    private static async Task CollectRemoteSourceTreeAsync(
        IRemoteFileSystem session, string remoteSourceDir, string localDestDir,
        List<(string Source, string Destination, long Size)> files, List<string> localDirs,
        CancellationToken token)
    {
        if (!RemotePathService.TryParse(remoteSourceDir, out _, out _, out var bareRemoteDir))
        {
            return;
        }

        foreach (var entry in await session.ListAsync(bareRemoteDir, token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();

            var childSource = RemotePathService.Combine(remoteSourceDir, entry.Name);
            var childDestination = Path.Combine(localDestDir, entry.Name);

            if (entry.IsDirectory)
            {
                localDirs.Add(childDestination);
                await CollectRemoteSourceTreeAsync(session, childSource, childDestination, files, localDirs, token).ConfigureAwait(false);
            }
            else
            {
                files.Add((childSource, childDestination, entry.Size));
            }
        }
    }

    private static async Task CollectLocalSourceTreeAsync(
        string localSourceDir, string remoteDestDir,
        List<(string Source, string Destination, long Size)> files, List<string> remoteDirs,
        CancellationToken token)
    {
        foreach (var dir in Directory.EnumerateDirectories(localSourceDir))
        {
            token.ThrowIfCancellationRequested();

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogWarning($"FileOperationQueueService.CollectLocalSourceTreeAsync (skipped dir): {dir}", ex);
                continue;
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            var childDestination = RemotePathService.Combine(remoteDestDir, Path.GetFileName(dir));
            remoteDirs.Add(childDestination);
            await CollectLocalSourceTreeAsync(dir, childDestination, files, remoteDirs, token).ConfigureAwait(false);
        }

        foreach (var file in Directory.EnumerateFiles(localSourceDir))
        {
            token.ThrowIfCancellationRequested();

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogWarning($"FileOperationQueueService.CollectLocalSourceTreeAsync (skipped file): {file}", ex);
                continue;
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            var childDestination = RemotePathService.Combine(remoteDestDir, Path.GetFileName(file));
            files.Add((file, childDestination, new FileInfo(file).Length));
        }
    }

    /// Walks the top-level selected items, asking Overwrite/Skip/Rename/Cancel for any that already
    /// exist at the destination (honoring "apply to all" once given), and returns the resolved
    /// (Source, Destination) pairs - Skip'd items excluded. An Overwrite choice sends the prior
    /// occupant to the Recycle Bin immediately so nothing stale is left for ResilientFileCopy's
    /// resume-offset logic to misinterpret as a partial copy.
    private async Task<List<(string Source, string Destination)>> ResolveTopLevelCollisionsAsync(
        IReadOnlyList<string> sourcePaths, string destinationFolder, CancellationToken token)
    {
        var resolved = new List<(string Source, string Destination)>();
        CollisionAction? appliedToAll = null;

        foreach (var source in sourcePaths)
        {
            token.ThrowIfCancellationRequested();

            var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar));
            var destination = Path.Combine(destinationFolder, name);

            if (!File.Exists(destination) && !Directory.Exists(destination))
            {
                resolved.Add((source, destination));
                continue;
            }

            CollisionAction action;
            string? renameTo;

            if (appliedToAll is { } applied)
            {
                action = applied;
                renameTo = null;
            }
            else
            {
                var resolution = await FileCollisionService.ResolveAsync(
                    name, FileOperationService.MakeUniqueDestination(destination), allowApplyToAll: true, _dispatcher, _getXamlRoot).ConfigureAwait(false);

                action = resolution.Action;
                renameTo = resolution.RenameTo;

                if (resolution.ApplyToAll)
                {
                    appliedToAll = action;
                }
            }

            switch (action)
            {
                case CollisionAction.Cancel:
                    throw new OperationCanceledException();

                case CollisionAction.Skip:
                    continue;

                case CollisionAction.Rename:
                    var renamed = !string.IsNullOrWhiteSpace(renameTo) ? Path.Combine(destinationFolder, renameTo) : destination;
                    resolved.Add((source, FileOperationService.MakeUniqueDestination(renamed)));
                    continue;

                case CollisionAction.Overwrite:
                    DeleteToRecycleBin(destination);
                    resolved.Add((source, destination));
                    continue;
            }
        }

        return resolved;
    }

    private static void DeleteToRecycleBin(string path)
    {
        if (Directory.Exists(path))
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        else if (File.Exists(path))
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
    }

    /// One-way, copy-only folder sync: copies anything in source that's missing from the target or
    /// newer than what's there. Never deletes or overwrites-away target-only files.
    private async Task RunFolderSyncAsync(FileOperationJob job, CancellationToken token)
    {
        var source = job.SourcePaths[0];
        var target = job.DestinationFolder;

        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Sync source folder no longer exists: {source}");
        }

        Directory.CreateDirectory(target);

        // Symbolic links/junctions are never followed - sync mirrors real file content, and
        // Directory.EnumerateFiles(AllDirectories) would otherwise follow a directory reparse
        // point into (and duplicate) whatever it points to, or loop forever on a self-referential
        // one.
        var files = EnumerateFilesSkippingLinks(source, job.IncludeHiddenSystemFiles).ToList();
        var toCopy = new List<(string Source, string Destination)>();
        long totalBytes = 0;
        CollisionAction? appliedToAll = null;

        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            var sourceInfo = new FileInfo(file);
            var destinationExists = File.Exists(destination);

            var needsCopy = !destinationExists || new FileInfo(destination).LastWriteTimeUtc < sourceInfo.LastWriteTimeUtc;
            if (!needsCopy)
            {
                continue;
            }

            // A file only in source (nothing at the destination yet) is normal sync growth, not a
            // collision - only prompt when there's already a differing file at this relative path.
            if (destinationExists)
            {
                CollisionAction action;
                string? renameTo;

                if (appliedToAll is { } applied)
                {
                    action = applied;
                    renameTo = null;
                }
                else
                {
                    var resolution = await FileCollisionService.ResolveAsync(
                        relative, FileOperationService.MakeUniqueDestination(destination), allowApplyToAll: true, _dispatcher, _getXamlRoot).ConfigureAwait(false);

                    action = resolution.Action;
                    renameTo = resolution.RenameTo;

                    if (resolution.ApplyToAll)
                    {
                        appliedToAll = action;
                    }
                }

                switch (action)
                {
                    case CollisionAction.Cancel:
                        throw new OperationCanceledException();

                    case CollisionAction.Skip:
                        continue;

                    case CollisionAction.Rename:
                        var renamed = !string.IsNullOrWhiteSpace(renameTo) ? Path.Combine(target, renameTo) : destination;
                        destination = FileOperationService.MakeUniqueDestination(renamed);
                        break;

                    case CollisionAction.Overwrite:
                        DeleteToRecycleBin(destination);
                        break;
                }
            }

            toCopy.Add((file, destination));
            totalBytes += sourceInfo.Length;
        }

        _dispatcher.TryEnqueue(() => job.BytesTotal = totalBytes);

        long bytesDoneTotal = 0;
        var progressLock = new object();

        await Parallel.ForEachAsync(
            toCopy,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelFiles, CancellationToken = token },
            async (file, ct) =>
            {
                _dispatcher.TryEnqueue(() => job.CurrentFileName = Path.GetRelativePath(source, file.Source));

                Directory.CreateDirectory(Path.GetDirectoryName(file.Destination)!);

                // A stale target file must be removed first: ResilientFileCopy treats an existing
                // destination as a partial/resumable copy from a previous attempt, and blindly
                // resuming into old content here would corrupt the file instead of replacing it.
                if (File.Exists(file.Destination))
                {
                    File.Delete(file.Destination);
                }

                await ResilientFileCopy.CopyFileAsync(
                    file.Source,
                    file.Destination,
                    delta =>
                    {
                        long done;
                        lock (progressLock)
                        {
                            bytesDoneTotal += delta;
                            done = bytesDoneTotal;
                        }

                        _dispatcher.TryEnqueue(() =>
                        {
                            job.BytesDone = done;
                            job.ProgressPercent = job.BytesTotal > 0 ? Math.Min(100, done * 100.0 / job.BytesTotal) : 100;
                        });
                    },
                    ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// destination is already collision-resolved (unique, or the prior occupant already cleared for
    /// an Overwrite) by ResolveTopLevelCollisionsAsync - this just performs the rename-move.
    private static bool MoveByRename(string source, string destination)
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else if (File.Exists(source))
        {
            File.Move(source, destination);
        }
        else
        {
            return false;
        }

        return true;
    }

    private static void DeleteSource(string source)
    {
        try
        {
            if (Directory.Exists(source))
            {
                Directory.Delete(source, recursive: true);
            }
            else if (File.Exists(source))
            {
                File.Delete(source);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.LogWarning("FileOperationQueueService.DeleteSource", ex);
        }
    }

    /// resolvedTopLevel is already collision-resolved (unique, or the prior occupant already cleared
    /// for an Overwrite) by ResolveTopLevelCollisionsAsync.
    /// Like Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories), but never descends
    /// into a reparse point (symbolic link/junction) and never yields one either - see
    /// CollectCopyEntries below for why this matters.
    private static IEnumerable<string> EnumerateFilesSkippingLinks(string directory, bool includeHiddenSystemFiles = true)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogWarning($"FileOperationQueueService.EnumerateFilesSkippingLinks (skipped): {entry}", ex);
                continue;
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            // A sync task's own choice: skip hidden/system files AND folders on the source side
            // entirely (so a hidden folder's contents are never even walked) unless opted in.
            if (!includeHiddenSystemFiles && (attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System)))
            {
                continue;
            }

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                foreach (var nested in EnumerateFilesSkippingLinks(entry, includeHiddenSystemFiles))
                {
                    yield return nested;
                }
            }
            else
            {
                yield return entry;
            }
        }
    }

    private static CopyPlan BuildCopyPlan(IReadOnlyList<(string Source, string Destination)> resolvedTopLevel)
    {
        var files = new List<(string Source, string Destination)>();
        var directories = new List<string>();
        var links = new List<(string Source, string Destination)>();
        var topLevel = new List<(string Source, string Destination)>();
        long total = 0;

        foreach (var (source, topDestination) in resolvedTopLevel)
        {
            topLevel.Add((source, topDestination));
            CollectCopyEntries(source, topDestination, files, directories, links, ref total);
        }

        return new CopyPlan(files, directories, links, topLevel, total);
    }

    /// Never descends into a reparse point (symbolic link or junction) - only recreates the link
    /// itself at the destination, pointing at the same target. Without this, a folder containing
    /// one would get followed into and its target's entire contents duplicated, or - for a
    /// self-referential link - recursed into forever.
    private static void CollectCopyEntries(
        string source,
        string destination,
        List<(string Source, string Destination)> files,
        List<string> directories,
        List<(string Source, string Destination)> links,
        ref long total)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.LogWarning($"FileOperationQueueService.CollectCopyEntries (skipped): {source}", ex);
            return;
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            links.Add((source, destination));
            return;
        }

        if (attributes.HasFlag(FileAttributes.Directory))
        {
            directories.Add(destination);

            foreach (var dir in Directory.EnumerateDirectories(source))
            {
                CollectCopyEntries(dir, Path.Combine(destination, Path.GetFileName(dir)), files, directories, links, ref total);
            }

            foreach (var file in Directory.EnumerateFiles(source))
            {
                CollectCopyEntries(file, Path.Combine(destination, Path.GetFileName(file)), files, directories, links, ref total);
            }
        }
        else
        {
            files.Add((source, destination));
            total += new FileInfo(source).Length;
        }
    }

    private sealed record CopyPlan(
        List<(string Source, string Destination)> Files,
        List<string> DirectoriesToCreate,
        List<(string Source, string Destination)> Links,
        List<(string Source, string Destination)> TopLevel,
        long TotalBytes);
}
