namespace FileExplorer.Models;

/// A target image format offered by "Convert To...".
public sealed record ConversionFormat(string Extension, string Display, bool IsLossy);

public enum FolderScanDepth
{
    /// Only image files directly inside each selected folder.
    DirectChildrenOnly,

    /// Every image file anywhere under each selected folder.
    Recurse,
}

public enum PostConversionAction
{
    KeepOriginal,

    /// Send the original to the Recycle Bin.
    DeleteOriginal,

    /// Move the original into an "Originals" subfolder next to where it was.
    MoveToOriginals,
}

public sealed record ConversionOptions(
    ConversionFormat Target,
    FolderScanDepth Depth,
    PostConversionAction PostAction,
    int Quality);

public enum ConversionStatus
{
    Converted,
    Skipped,
    Failed,
}

public sealed record ConversionOutcome(string SourcePath, ConversionStatus Status, string? DestinationPath, string? Message);
