namespace FileExplorer.Models;

/// One data point from a disk benchmark run: a given test-file size, I/O pattern, and direction.
/// Unbuffered is false when the unbuffered (FILE_FLAG_NO_BUFFERING) path failed for this specific
/// operation and the buffered FileStream fallback ran instead - that result may be inflated by
/// Windows' file cache rather than reflecting real disk speed, and the UI flags it as such.
public sealed record BenchmarkResult(string SizeLabel, long SizeBytes, string Pattern, string Direction, double ThroughputMBps, double DurationMs, bool Unbuffered, string? FallbackReason);
