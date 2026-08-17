using FileExplorer.Helpers;
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

public sealed class FileOperationJob : ObservableObject
{
    private FileOperationStatus _status = FileOperationStatus.Queued;
    private double _progressPercent;
    private string _currentFileName = string.Empty;
    private long _bytesDone;
    private long _bytesTotal;
    private string? _errorMessage;

    public required Guid Id { get; init; }
    public required FileDropOperation Kind { get; init; }
    public required IReadOnlyList<string> SourcePaths { get; init; }
    public required string DestinationFolder { get; init; }

    public CancellationTokenSource CancellationTokenSource { get; } = new();

    public string Title => $"{(Kind == FileDropOperation.Move ? "Move" : "Copy")} {SourcePaths.Count} item{(SourcePaths.Count == 1 ? "" : "s")} to {System.IO.Path.GetFileName(DestinationFolder.TrimEnd('\\'))}";

    public FileOperationStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(CanCancel));
            }
        }
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        set => SetProperty(ref _progressPercent, value);
    }

    public string CurrentFileName
    {
        get => _currentFileName;
        set => SetProperty(ref _currentFileName, value);
    }

    public long BytesDone
    {
        get => _bytesDone;
        set => SetProperty(ref _bytesDone, value);
    }

    public long BytesTotal
    {
        get => _bytesTotal;
        set => SetProperty(ref _bytesTotal, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public bool CanCancel => Status is FileOperationStatus.Queued or FileOperationStatus.Running;
}
