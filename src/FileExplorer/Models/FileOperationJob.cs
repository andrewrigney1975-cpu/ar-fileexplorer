using CommunityToolkit.Mvvm.ComponentModel;
using FileExplorer.Services;

namespace FileExplorer.Models;

public enum FileOperationStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Canceled,
}

public sealed partial class FileOperationJob : ObservableObject
{
    public required Guid Id { get; init; }
    public required FileDropOperation Kind { get; init; }
    public required IReadOnlyList<string> SourcePaths { get; init; }
    public required string DestinationFolder { get; init; }

    /// True when the destination folder was just created to receive this job (e.g. "Move to new
    /// folder..."), so undoing the move can also remove that folder once it's empty again.
    public bool DestinationWasCreatedForThisJob { get; init; }

    /// Set only for Kind == Sync; the sync task's user-chosen name, used in its title and notification text.
    public string? SyncTaskName { get; init; }

    /// Set only for Kind == Sync, from the task's own setting. When false (the default), hidden
    /// and system files/folders on the source side are skipped entirely rather than mirrored.
    public bool IncludeHiddenSystemFiles { get; init; }

    public CancellationTokenSource CancellationTokenSource { get; } = new();

    public string Title => Kind switch
    {
        FileDropOperation.Sync => $"Sync: {SyncTaskName}",
        FileDropOperation.Move => $"Move {SourcePaths.Count} item{(SourcePaths.Count == 1 ? "" : "s")} to {System.IO.Path.GetFileName(DestinationFolder.TrimEnd('\\'))}",
        _ => $"Copy {SourcePaths.Count} item{(SourcePaths.Count == 1 ? "" : "s")} to {System.IO.Path.GetFileName(DestinationFolder.TrimEnd('\\'))}",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    public partial FileOperationStatus Status { get; set; } = FileOperationStatus.Queued;

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string CurrentFileName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial long BytesDone { get; set; }

    [ObservableProperty]
    public partial long BytesTotal { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    public bool CanCancel => Status is FileOperationStatus.Queued or FileOperationStatus.Running;
}
