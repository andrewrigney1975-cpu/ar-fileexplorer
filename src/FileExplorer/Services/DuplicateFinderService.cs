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
}
