namespace FileExplorer.Models;

/// A pinned LAN share, browsed directly by its UNC path (no drive letter needed).
public sealed record NetworkLocation(string Name, string UncPath);
