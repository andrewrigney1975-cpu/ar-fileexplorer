using System.Collections.ObjectModel;
using FileExplorer.Models;
using Microsoft.UI.Dispatching;

namespace FileExplorer.Services;

/// Serializes drag-and-drop file operations into a background queue: same-drive moves are instant
/// renames, everything else streams through the resilient, parallel copy engine.
public sealed class FileOperationQueueService
{
    private const int MaxParallelFiles = 4;

    private readonly DispatcherQueue _dispatcher;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Queue<FileOperationJob> _pending = new();
    private readonly object _lock = new();

    public FileOperationQueueService(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
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
            var allSameDrive = job.SourcePaths.All(p => FileOperationService.SameDrive(p, job.DestinationFolder));

            if (job.Kind == FileDropOperation.Move && allSameDrive)
            {
                // Same-volume move is an atomic filesystem rename - already optimal, no streaming needed.
                var moved = new List<(string Source, string Destination)>();
                foreach (var source in job.SourcePaths)
                {
                    token.ThrowIfCancellationRequested();
                    var destination = MoveByRename(source, job.DestinationFolder);
                    if (destination is not null)
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
                var plan = BuildCopyPlan(job.SourcePaths, job.DestinationFolder);
                _dispatcher.TryEnqueue(() => job.BytesTotal = plan.TotalBytes);

                foreach (var dir in plan.DirectoriesToCreate)
                {
                    Directory.CreateDirectory(dir);
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
                    foreach (var source in job.SourcePaths)
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

    private static string? MoveByRename(string source, string destinationFolder)
    {
        var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar));
        var destination = FileOperationService.MakeUniqueDestination(Path.Combine(destinationFolder, name));

        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            return null;
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
            return null;
        }

        return destination;
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
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static CopyPlan BuildCopyPlan(IReadOnlyList<string> sourcePaths, string destinationFolder)
    {
        var files = new List<(string Source, string Destination)>();
        var directories = new List<string>();
        var topLevel = new List<(string Source, string Destination)>();
        long total = 0;

        foreach (var source in sourcePaths)
        {
            var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar));
            var topDestination = FileOperationService.MakeUniqueDestination(Path.Combine(destinationFolder, name));
            topLevel.Add((source, topDestination));

            if (Directory.Exists(source))
            {
                directories.Add(topDestination);

                foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(source, dir);
                    directories.Add(Path.Combine(topDestination, relative));
                }

                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(source, file);
                    var destination = Path.Combine(topDestination, relative);
                    files.Add((file, destination));
                    total += new FileInfo(file).Length;
                }
            }
            else if (File.Exists(source))
            {
                files.Add((source, topDestination));
                total += new FileInfo(source).Length;
            }
        }

        return new CopyPlan(files, directories, topLevel, total);
    }

    private sealed record CopyPlan(
        List<(string Source, string Destination)> Files,
        List<string> DirectoriesToCreate,
        List<(string Source, string Destination)> TopLevel,
        long TotalBytes);
}
