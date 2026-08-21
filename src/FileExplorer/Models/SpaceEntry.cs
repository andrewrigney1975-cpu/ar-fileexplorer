namespace FileExplorer.Models;

/// One immediate child of the folder currently shown in the Disk Space Analyser: a donut-chart
/// slice, a row in the side list, and a row in the JSON export, all from the same data.
public sealed record SpaceEntry(string Name, string FullPath, bool IsDirectory, long SizeBytes, int ItemCount);
