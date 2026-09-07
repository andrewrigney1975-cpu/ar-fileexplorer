using System.Security.Cryptography;

namespace FileExplorer.Services;

/// Recursive duplicate-file scan: groups by size first (cheap), then hashes only files that
/// share a size, so most of a tree never needs to be read at all.
public static class DuplicateFinderService
{
    public static Task<List<List<string>>> FindDuplicatesAsync(string rootPath, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var bySize = new Dictionary<long, List<string>>();

            foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                long length;
                try
                {
                    length = new FileInfo(file).Length;
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                if (length == 0)
                {
                    continue; // empty files are trivially "identical" and not a useful result
                }

                if (!bySize.TryGetValue(length, out var sameSize))
                {
                    sameSize = new List<string>();
                    bySize[length] = sameSize;
                }

                sameSize.Add(file);
            }

            var duplicateGroups = new List<List<string>>();

            foreach (var candidates in bySize.Values)
            {
                if (candidates.Count < 2)
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();

                var byHash = new Dictionary<string, List<string>>();
                foreach (var file in candidates)
                {
                    string hash;
                    try
                    {
                        using var stream = File.OpenRead(file);
                        hash = Convert.ToHexString(SHA256.HashData(stream));
                    }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }

                    if (!byHash.TryGetValue(hash, out var sameHash))
                    {
                        sameHash = new List<string>();
                        byHash[hash] = sameHash;
                    }

                    sameHash.Add(file);
                }

                duplicateGroups.AddRange(byHash.Values.Where(g => g.Count > 1));
            }

            return duplicateGroups;
        }, cancellationToken);
    }

    /// Index-backed duplicate scan: groups files using the size and MD5 hash the search index already
    /// stores, so most of the tree is never touched on disk. Only valid when the location is covered
    /// by the search index - see SearchIndexService.IsPathIndexed. Files the index has no hash for
    /// (hashing was skipped, too slow, or failed during indexing) are hashed from disk here so the
    /// result is still complete, and index rows whose file has since vanished are dropped.
    public static Task<List<List<string>>> FindDuplicatesFromIndexAsync(string rootPath, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var bySize = new Dictionary<long, List<IndexedFile>>();

            foreach (var file in SearchIndexService.GetIndexedFilesUnder(rootPath))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (file.SizeBytes == 0 || !File.Exists(file.Path))
                {
                    continue;
                }

                if (!bySize.TryGetValue(file.SizeBytes, out var sameSize))
                {
                    sameSize = new List<IndexedFile>();
                    bySize[file.SizeBytes] = sameSize;
                }

                sameSize.Add(file);
            }

            var duplicateGroups = new List<List<string>>();

            foreach (var candidates in bySize.Values)
            {
                if (candidates.Count < 2)
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();

                var byHash = new Dictionary<string, List<string>>();
                foreach (var file in candidates)
                {
                    // A valid indexed hash is 32 hex chars; anything else (null = not yet backfilled,
                    // "" = backfill tried and the file was unreadable) means hash it from disk now.
                    var hash = file.Md5Hash is { Length: 32 } indexed ? indexed : HashFromDisk(file.Path);
                    if (hash is null)
                    {
                        continue;
                    }

                    if (!byHash.TryGetValue(hash, out var sameHash))
                    {
                        sameHash = new List<string>();
                        byHash[hash] = sameHash;
                    }

                    sameHash.Add(file.Path);
                }

                duplicateGroups.AddRange(byHash.Values.Where(g => g.Count > 1));
            }

            return duplicateGroups;
        }, cancellationToken);
    }

    /// MD5 of a file read from disk, matching the hash the index stores, so index-supplied and
    /// disk-computed hashes are directly comparable within one scan.
    private static string? HashFromDisk(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(MD5.HashData(stream));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
