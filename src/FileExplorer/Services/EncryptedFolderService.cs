using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileExplorer.Services;

/// Encrypts a real folder tree into a single opaque container file ("Name.dxlock", replacing the
/// original folder in its own parent directory) and provides decrypt-on-demand reads out of it -
/// see the "Virtual Folders" -> "Encrypted Folders" plan: content, names, and structure are all
/// AES-256-GCM encrypted; nothing about the tree is recoverable from the container without the PIN.
///
/// Container layout (all integers little-endian):
///   Header (72 bytes, fixed, at offset 0):
///     Magic(8) Salt(16) Iterations(4) CheckNonce(12) CheckCiphertext(16) CheckTag(16)
///   Data section (starts at offset 72): each file's AES-GCM chunk ciphertexts, back to back,
///     in walk order - chunk nonces/tags/lengths live in the index, not inline, so a single
///     file's bytes can be located and decrypted without touching any other file's data.
///   Encrypted index: IndexNonce(12) + IndexCiphertext(N) + IndexTag(16) - a JSON-serialized
///     List&lt;IndexEntry&gt; describing the whole tree (relative paths, sizes, attributes,
///     per-file chunk list with each chunk's own nonce/tag/length, and each file's byte offset
///     into the data section).
///   Footer (24 bytes, fixed, at end of file): IndexSectionOffset(8) IndexCiphertextLength(8)
///     FooterMagic(8) - read from the end first, so opening the container never requires reading
///     (let alone decrypting) the data section at all.
///
/// PBKDF2-SHA256 (iteration count stored in the header, not hardcoded at read time, so it can be
/// raised later without breaking old containers) derives the AES-256 key from the PIN; the salt is
/// stored in the clear (salts aren't secret - they only stop rainbow-table reuse across containers).
/// A wrong PIN is rejected in milliseconds via the small CheckCiphertext/CheckTag pair, never by
/// attempting to decrypt the (potentially huge) index or data.
public static class EncryptedFolderService
{
    public const string ContainerExtension = ".dxlock";

    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int DefaultIterations = 600_000;
    private const long ChunkSize = 64L * 1024 * 1024;

    private static readonly byte[] MagicHeader = Encoding.ASCII.GetBytes("DXLOCK01");
    private static readonly byte[] FooterMagic = Encoding.ASCII.GetBytes("DXFOOT01");
    private static readonly byte[] CheckPlaintext = Encoding.ASCII.GetBytes("DXLOCKPINCHECK16");

    private const int HeaderSize = 8 /*magic*/ + SaltSize + 4 /*iterations*/ + NonceSize + 16 /*check ciphertext*/ + TagSize;
    private const int FooterSize = 8 /*index offset*/ + 8 /*index ciphertext length*/ + 8 /*footer magic*/;

    public sealed record ChunkInfo(byte[] Nonce, byte[] Tag, int Length);

    public sealed record IndexEntry(
        string RelativePath,
        bool IsDirectory,
        long SizeBytes,
        int Attributes,
        long ModifiedUtcTicks,
        long DataStartOffset,
        List<ChunkInfo> Chunks);

    private sealed class WalkEntry
    {
        public required string FullPath { get; init; }
        public required string RelativePath { get; init; }
        public required bool IsDirectory { get; init; }
        public long SizeBytes { get; init; }
        public FileAttributes Attributes { get; init; }
        public long ModifiedUtcTicks { get; init; }
    }

    /// Encrypts <paramref name="folderPath"/> into a sibling "Name.dxlock" container, verifies the
    /// freshly-written container can actually be opened with the same PIN before touching anything,
    /// then best-effort wipes the original files and deletes the folder. Returns the container's path.
    public static async Task<string> EncryptFolderAsync(string folderPath, string pin, IProgress<string>? progress, CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(folderPath.TrimEnd(Path.DirectorySeparatorChar))
            ?? throw new InvalidOperationException("Folder has no parent to place the container in.");
        var baseName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar));
        var containerPath = UniquePath(Path.Combine(parent, baseName + ContainerExtension));
        var tempPath = containerPath + ".tmp";

        var walk = new List<WalkEntry>();
        WalkRecursive(folderPath, string.Empty, walk);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = DeriveKey(pin, salt, DefaultIterations);

        var checkNonce = RandomNumberGenerator.GetBytes(NonceSize);
        var checkCiphertext = new byte[CheckPlaintext.Length];
        var checkTag = new byte[TagSize];
        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(checkNonce, CheckPlaintext, checkCiphertext, checkTag);
        }

        var indexEntries = new List<IndexEntry>();
        long runningOffset = 0;

        try
        {
            await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await WriteHeaderAsync(output, salt, checkNonce, checkCiphertext, checkTag, ct);

                foreach (var entry in walk)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(entry.RelativePath);

                    if (entry.IsDirectory)
                    {
                        indexEntries.Add(new IndexEntry(entry.RelativePath, true, 0, (int)entry.Attributes, entry.ModifiedUtcTicks, 0, new List<ChunkInfo>()));
                        continue;
                    }

                    var chunks = new List<ChunkInfo>();
                    var dataStart = runningOffset;

                    using var aes = new AesGcm(key, TagSize);
                    await using var input = new FileStream(entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var buffer = new byte[ChunkSize];
                    var remaining = entry.SizeBytes;

                    while (remaining > 0)
                    {
                        var take = (int)Math.Min(ChunkSize, remaining);
                        await ReadExactAsync(input, buffer, take, ct);

                        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
                        var ciphertext = new byte[take];
                        var tag = new byte[TagSize];
                        aes.Encrypt(nonce, buffer.AsSpan(0, take), ciphertext, tag);

                        await output.WriteAsync(ciphertext, ct);
                        chunks.Add(new ChunkInfo(nonce, tag, take));
                        runningOffset += take;
                        remaining -= take;
                    }

                    indexEntries.Add(new IndexEntry(entry.RelativePath, false, entry.SizeBytes, (int)entry.Attributes, entry.ModifiedUtcTicks, dataStart, chunks));
                }

                var indexJson = JsonSerializer.SerializeToUtf8Bytes(indexEntries);
                var indexNonce = RandomNumberGenerator.GetBytes(NonceSize);
                var indexCiphertext = new byte[indexJson.Length];
                var indexTag = new byte[TagSize];
                using (var aes = new AesGcm(key, TagSize))
                {
                    aes.Encrypt(indexNonce, indexJson, indexCiphertext, indexTag);
                }

                var indexSectionOffset = output.Position;
                await output.WriteAsync(indexNonce, ct);
                await output.WriteAsync(indexCiphertext, ct);
                await output.WriteAsync(indexTag, ct);

                var footer = new byte[FooterSize];
                BitConverter.TryWriteBytes(footer.AsSpan(0, 8), indexSectionOffset);
                BitConverter.TryWriteBytes(footer.AsSpan(8, 8), (long)indexCiphertext.Length);
                FooterMagic.CopyTo(footer.AsSpan(16, 8));
                await output.WriteAsync(footer, ct);
            }

            var verified = await TryOpenAsync(tempPath, pin, ct);
            if (verified is null || verified.Value.Entries.Count != indexEntries.Count)
            {
                throw new InvalidOperationException("The encrypted container failed verification - the original folder was not touched.");
            }

            File.Move(tempPath, containerPath, overwrite: false);
        }
        catch
        {
            try { File.Delete(tempPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }

        WipeDirectoryBestEffort(folderPath);
        return containerPath;
    }

    /// Attempts to open a container with the given PIN - null means either a wrong PIN (the fast
    /// CheckCiphertext/CheckTag pair failed to authenticate) or a corrupt/foreign file.
    public static async Task<(byte[] Key, List<IndexEntry> Entries)?> TryOpenAsync(string containerPath, string pin, CancellationToken ct)
    {
        await using var file = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var header = new byte[HeaderSize];
        await ReadExactAsync(file, header, HeaderSize, ct);
        if (!header.AsSpan(0, MagicHeader.Length).SequenceEqual(MagicHeader))
        {
            return null;
        }

        var offset = MagicHeader.Length;
        var salt = header.AsSpan(offset, SaltSize).ToArray(); offset += SaltSize;
        var iterations = BitConverter.ToInt32(header, offset); offset += 4;
        var checkNonce = header.AsSpan(offset, NonceSize).ToArray(); offset += NonceSize;
        var checkCiphertext = header.AsSpan(offset, CheckPlaintext.Length).ToArray(); offset += CheckPlaintext.Length;
        var checkTag = header.AsSpan(offset, TagSize).ToArray();

        var key = DeriveKey(pin, salt, iterations);
        var checkPlain = new byte[CheckPlaintext.Length];

        try
        {
            using var checkAes = new AesGcm(key, TagSize);
            checkAes.Decrypt(checkNonce, checkCiphertext, checkTag, checkPlain);
        }
        catch (CryptographicException)
        {
            return null; // wrong PIN
        }

        file.Seek(-FooterSize, SeekOrigin.End);
        var footer = new byte[FooterSize];
        await ReadExactAsync(file, footer, FooterSize, ct);
        if (!footer.AsSpan(16, 8).SequenceEqual(FooterMagic))
        {
            return null; // corrupt/truncated container
        }

        var indexOffset = BitConverter.ToInt64(footer, 0);
        var indexCiphertextLength = (int)BitConverter.ToInt64(footer, 8);

        file.Seek(indexOffset, SeekOrigin.Begin);
        var indexNonce = new byte[NonceSize];
        await ReadExactAsync(file, indexNonce, NonceSize, ct);
        var indexCiphertext = new byte[indexCiphertextLength];
        await ReadExactAsync(file, indexCiphertext, indexCiphertextLength, ct);
        var indexTag = new byte[TagSize];
        await ReadExactAsync(file, indexTag, TagSize, ct);

        var indexPlain = new byte[indexCiphertextLength];
        using (var indexAes = new AesGcm(key, TagSize))
        {
            indexAes.Decrypt(indexNonce, indexCiphertext, indexTag, indexPlain);
        }

        var entries = JsonSerializer.Deserialize<List<IndexEntry>>(indexPlain) ?? new List<IndexEntry>();
        return (key, entries);
    }

    /// Streams one file's plaintext bytes out of the container to <paramref name="destinationPath"/>,
    /// chunk by chunk - never loads a whole large file into memory.
    public static async Task DecryptEntryToFileAsync(string containerPath, byte[] key, IndexEntry entry, string destinationPath, CancellationToken ct)
    {
        await using var file = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        file.Seek(HeaderSize + entry.DataStartOffset, SeekOrigin.Begin);

        await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var aes = new AesGcm(key, TagSize);

        foreach (var chunk in entry.Chunks)
        {
            var ciphertext = new byte[chunk.Length];
            await ReadExactAsync(file, ciphertext, chunk.Length, ct);
            var plaintext = new byte[chunk.Length];
            aes.Decrypt(chunk.Nonce, ciphertext, chunk.Tag, plaintext);
            await output.WriteAsync(plaintext, ct);
        }
    }

    /// Decrypts an entire container back to a real folder tree (the explicit, permanent "remove
    /// encryption" escape hatch) - the container itself is left untouched so a failure partway
    /// through can't lose data; callers delete the container only after this returns successfully.
    public static async Task DecryptAllAsync(string containerPath, byte[] key, IReadOnlyList<IndexEntry> entries, string destinationFolder, CancellationToken ct)
    {
        Directory.CreateDirectory(destinationFolder);

        foreach (var entry in entries.Where(e => e.IsDirectory))
        {
            Directory.CreateDirectory(Path.Combine(destinationFolder, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        }

        foreach (var entry in entries.Where(e => !e.IsDirectory))
        {
            ct.ThrowIfCancellationRequested();
            var destPath = Path.Combine(destinationFolder, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            await DecryptEntryToFileAsync(containerPath, key, entry, destPath, ct);
            File.SetLastWriteTimeUtc(destPath, new DateTime(entry.ModifiedUtcTicks, DateTimeKind.Utc));
        }
    }

    /// Rebuilds the container from scratch with <paramref name="newEntries"/> as its new index:
    /// unchanged files' ciphertext is streamed byte-for-byte from the old container (their nonces/
    /// tags stay valid since the bytes never change), while any entry named in
    /// <paramref name="newFileSources"/> is freshly encrypted from that real source file. Writes to
    /// a ".tmp" sibling, verifies it opens with <paramref name="key"/> and matches the expected
    /// entry count, then atomically replaces the container - the same verify-before-swap pattern as
    /// EncryptFolderAsync, so a failure or crash mid-rewrite never corrupts or loses the original.
    /// Because every add/delete goes through this and only ever emits bytes for entries present in
    /// <paramref name="newEntries"/>, the container never actually accumulates dead space between
    /// calls - a delete already "compacts" as a side effect. CompactAsync exists anyway as an
    /// explicit, user-triggered no-op-if-nothing-to-do safety valve.
    private static async Task<List<IndexEntry>> RewriteContainerAsync(
        string containerPath,
        byte[] key,
        List<IndexEntry> oldEntries,
        List<IndexEntry> newEntries,
        IReadOnlyDictionary<string, string> newFileSources,
        CancellationToken ct)
    {
        var tempPath = containerPath + ".tmp";
        var oldEntryByPath = oldEntries.ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
        var rebuiltEntries = new List<IndexEntry>();

        try
        {
            byte[] header;
            await using (var oldFile = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                header = new byte[HeaderSize];
                await ReadExactAsync(oldFile, header, HeaderSize, ct);

                await using var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await output.WriteAsync(header, ct);

                long runningOffset = 0;
                var copyBuffer = new byte[81920];

                foreach (var entry in newEntries)
                {
                    ct.ThrowIfCancellationRequested();

                    if (entry.IsDirectory)
                    {
                        rebuiltEntries.Add(entry with { DataStartOffset = 0 });
                        continue;
                    }

                    if (newFileSources.TryGetValue(entry.RelativePath, out var sourcePath))
                    {
                        var dataStart = runningOffset;
                        var chunks = new List<ChunkInfo>();
                        using var aes = new AesGcm(key, TagSize);
                        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        var buffer = new byte[ChunkSize];
                        var remaining = entry.SizeBytes;

                        while (remaining > 0)
                        {
                            var take = (int)Math.Min(ChunkSize, remaining);
                            await ReadExactAsync(input, buffer, take, ct);

                            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
                            var ciphertext = new byte[take];
                            var tag = new byte[TagSize];
                            aes.Encrypt(nonce, buffer.AsSpan(0, take), ciphertext, tag);

                            await output.WriteAsync(ciphertext, ct);
                            chunks.Add(new ChunkInfo(nonce, tag, take));
                            runningOffset += take;
                            remaining -= take;
                        }

                        rebuiltEntries.Add(entry with { DataStartOffset = dataStart, Chunks = chunks });
                    }
                    else
                    {
                        var oldEntry = oldEntryByPath[entry.RelativePath];
                        var dataStart = runningOffset;
                        var totalLength = oldEntry.Chunks.Sum(c => (long)c.Length);

                        oldFile.Seek(HeaderSize + oldEntry.DataStartOffset, SeekOrigin.Begin);
                        var remaining = totalLength;
                        while (remaining > 0)
                        {
                            var take = (int)Math.Min(copyBuffer.Length, remaining);
                            await ReadExactAsync(oldFile, copyBuffer, take, ct);
                            await output.WriteAsync(copyBuffer.AsMemory(0, take), ct);
                            remaining -= take;
                        }

                        runningOffset += totalLength;
                        rebuiltEntries.Add(entry with { DataStartOffset = dataStart });
                    }
                }

                var indexJson = JsonSerializer.SerializeToUtf8Bytes(rebuiltEntries);
                var indexNonce = RandomNumberGenerator.GetBytes(NonceSize);
                var indexCiphertext = new byte[indexJson.Length];
                var indexTag = new byte[TagSize];
                using (var aes = new AesGcm(key, TagSize))
                {
                    aes.Encrypt(indexNonce, indexJson, indexCiphertext, indexTag);
                }

                var indexSectionOffset = output.Position;
                await output.WriteAsync(indexNonce, ct);
                await output.WriteAsync(indexCiphertext, ct);
                await output.WriteAsync(indexTag, ct);

                var footer = new byte[FooterSize];
                BitConverter.TryWriteBytes(footer.AsSpan(0, 8), indexSectionOffset);
                BitConverter.TryWriteBytes(footer.AsSpan(8, 8), (long)indexCiphertext.Length);
                FooterMagic.CopyTo(footer.AsSpan(16, 8));
                await output.WriteAsync(footer, ct);
            }

            var verified = await TryOpenWithKeyAsync(tempPath, key, ct);
            if (verified is null || verified.Count != rebuiltEntries.Count)
            {
                throw new InvalidOperationException("The rewritten encrypted container failed verification - the original was not touched.");
            }

            File.Delete(containerPath);
            File.Move(tempPath, containerPath, overwrite: false);
        }
        catch
        {
            try { File.Delete(tempPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }

        return rebuiltEntries;
    }

    /// Same authentication guarantee as TryOpenAsync (the index only decrypts if the key is right
    /// and every byte is intact) but takes the already-derived key directly instead of re-deriving
    /// it from a PIN - used to verify a freshly rewritten container without ever needing the PIN
    /// again after the initial unlock.
    private static async Task<List<IndexEntry>?> TryOpenWithKeyAsync(string containerPath, byte[] key, CancellationToken ct)
    {
        await using var file = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        file.Seek(-FooterSize, SeekOrigin.End);
        var footer = new byte[FooterSize];
        await ReadExactAsync(file, footer, FooterSize, ct);
        if (!footer.AsSpan(16, 8).SequenceEqual(FooterMagic))
        {
            return null;
        }

        var indexOffset = BitConverter.ToInt64(footer, 0);
        var indexCiphertextLength = (int)BitConverter.ToInt64(footer, 8);

        file.Seek(indexOffset, SeekOrigin.Begin);
        var indexNonce = new byte[NonceSize];
        await ReadExactAsync(file, indexNonce, NonceSize, ct);
        var indexCiphertext = new byte[indexCiphertextLength];
        await ReadExactAsync(file, indexCiphertext, indexCiphertextLength, ct);
        var indexTag = new byte[TagSize];
        await ReadExactAsync(file, indexTag, TagSize, ct);

        var indexPlain = new byte[indexCiphertextLength];
        try
        {
            using var indexAes = new AesGcm(key, TagSize);
            indexAes.Decrypt(indexNonce, indexCiphertext, indexTag, indexPlain);
        }
        catch (CryptographicException)
        {
            return null;
        }

        return JsonSerializer.Deserialize<List<IndexEntry>>(indexPlain) ?? new List<IndexEntry>();
    }

    /// Adds one real file as a new entry under <paramref name="parentRelativeKey"/> ("" for the
    /// container root). Throws IOException if an entry with that name already exists there.
    public static Task<List<IndexEntry>> AddFileEntryAsync(
        string containerPath, byte[] key, List<IndexEntry> entries, string parentRelativeKey, string sourceFilePath, CancellationToken ct)
    {
        var info = new FileInfo(sourceFilePath);
        var relKey = parentRelativeKey.Length == 0 ? info.Name : parentRelativeKey + "/" + info.Name;

        if (entries.Any(e => string.Equals(e.RelativePath, relKey, StringComparison.Ordinal)))
        {
            throw new IOException($"\"{info.Name}\" already exists in this encrypted folder.");
        }

        var newEntry = new IndexEntry(relKey, false, info.Length, (int)info.Attributes, info.LastWriteTimeUtc.Ticks, 0, new List<ChunkInfo>());
        var newEntries = new List<IndexEntry>(entries) { newEntry };
        var sources = new Dictionary<string, string> { [relKey] = sourceFilePath };

        return RewriteContainerAsync(containerPath, key, entries, newEntries, sources, ct);
    }

    /// Recursively adds a real folder (and everything inside it) as a new subtree under
    /// <paramref name="parentRelativeKey"/>. Throws IOException if an entry with that name already
    /// exists there.
    public static Task<List<IndexEntry>> AddFolderTreeEntriesAsync(
        string containerPath, byte[] key, List<IndexEntry> entries, string parentRelativeKey, string sourceFolderPath, CancellationToken ct)
    {
        var rootName = Path.GetFileName(sourceFolderPath.TrimEnd(Path.DirectorySeparatorChar));
        var rootRel = parentRelativeKey.Length == 0 ? rootName : parentRelativeKey + "/" + rootName;

        if (entries.Any(e => string.Equals(e.RelativePath, rootRel, StringComparison.Ordinal)))
        {
            throw new IOException($"\"{rootName}\" already exists in this encrypted folder.");
        }

        var walk = new List<WalkEntry>();
        var rootInfo = new DirectoryInfo(sourceFolderPath);
        walk.Add(new WalkEntry { FullPath = sourceFolderPath, RelativePath = rootRel, IsDirectory = true, Attributes = rootInfo.Attributes, ModifiedUtcTicks = rootInfo.LastWriteTimeUtc.Ticks });
        WalkRecursive(sourceFolderPath, rootRel, walk);

        var newEntries = new List<IndexEntry>(entries);
        var sources = new Dictionary<string, string>();

        foreach (var w in walk)
        {
            if (w.IsDirectory)
            {
                newEntries.Add(new IndexEntry(w.RelativePath, true, 0, (int)w.Attributes, w.ModifiedUtcTicks, 0, new List<ChunkInfo>()));
            }
            else
            {
                newEntries.Add(new IndexEntry(w.RelativePath, false, w.SizeBytes, (int)w.Attributes, w.ModifiedUtcTicks, 0, new List<ChunkInfo>()));
                sources[w.RelativePath] = w.FullPath;
            }
        }

        return RewriteContainerAsync(containerPath, key, entries, newEntries, sources, ct);
    }

    /// Adds a new, empty directory entry under <paramref name="parentRelativeKey"/>. Throws
    /// IOException if an entry with that name already exists there.
    public static Task<List<IndexEntry>> AddEmptyFolderEntryAsync(
        string containerPath, byte[] key, List<IndexEntry> entries, string parentRelativeKey, string name, CancellationToken ct)
    {
        var relKey = parentRelativeKey.Length == 0 ? name : parentRelativeKey + "/" + name;

        if (entries.Any(e => string.Equals(e.RelativePath, relKey, StringComparison.Ordinal)))
        {
            throw new IOException($"\"{name}\" already exists in this encrypted folder.");
        }

        var newEntry = new IndexEntry(relKey, true, 0, (int)FileAttributes.Directory, DateTime.UtcNow.Ticks, 0, new List<ChunkInfo>());
        var newEntries = new List<IndexEntry>(entries) { newEntry };

        return RewriteContainerAsync(containerPath, key, entries, newEntries, new Dictionary<string, string>(), ct);
    }

    /// Removes the entry at <paramref name="relativeKey"/> - and, if it's a directory, every entry
    /// nested under it - from the index and rebuilds the container without their data.
    public static Task<List<IndexEntry>> RemoveEntryAsync(
        string containerPath, byte[] key, List<IndexEntry> entries, string relativeKey, CancellationToken ct)
    {
        var prefix = relativeKey + "/";
        var newEntries = entries
            .Where(e => !string.Equals(e.RelativePath, relativeKey, StringComparison.Ordinal) &&
                        !e.RelativePath.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();

        if (newEntries.Count == entries.Count)
        {
            throw new FileNotFoundException("That item is no longer in the encrypted folder's index.", relativeKey);
        }

        return RewriteContainerAsync(containerPath, key, entries, newEntries, new Dictionary<string, string>(), ct);
    }

    /// Rebuilds the container with the same entries it already has - a manual, explicit "reclaim any
    /// dead space" action. In practice every add/delete already rebuilds without dead bytes, so this
    /// is normally a no-op; it exists as a user-triggered safety valve rather than something the app
    /// needs to rely on internally.
    public static Task<List<IndexEntry>> CompactAsync(string containerPath, byte[] key, List<IndexEntry> entries, CancellationToken ct) =>
        RewriteContainerAsync(containerPath, key, entries, entries, new Dictionary<string, string>(), ct);

    private static byte[] DeriveKey(string pin, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, 32);

    private static async Task WriteHeaderAsync(Stream output, byte[] salt, byte[] checkNonce, byte[] checkCiphertext, byte[] checkTag, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        var offset = 0;
        MagicHeader.CopyTo(header.AsSpan(offset, MagicHeader.Length)); offset += MagicHeader.Length;
        salt.CopyTo(header.AsSpan(offset, SaltSize)); offset += SaltSize;
        BitConverter.TryWriteBytes(header.AsSpan(offset, 4), DefaultIterations); offset += 4;
        checkNonce.CopyTo(header.AsSpan(offset, NonceSize)); offset += NonceSize;
        checkCiphertext.CopyTo(header.AsSpan(offset, checkCiphertext.Length)); offset += checkCiphertext.Length;
        checkTag.CopyTo(header.AsSpan(offset, TagSize));
        await output.WriteAsync(header, ct);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (read == 0)
            {
                throw new EndOfStreamException("Encrypted container ended unexpectedly - it may be corrupt or truncated.");
            }

            offset += read;
        }
    }

    private static void WalkRecursive(string root, string relativePrefix, List<WalkEntry> results)
    {
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            var rel = relativePrefix.Length == 0 ? name : relativePrefix + "/" + name;
            var info = new DirectoryInfo(dir);
            results.Add(new WalkEntry { FullPath = dir, RelativePath = rel, IsDirectory = true, Attributes = info.Attributes, ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks });
            WalkRecursive(dir, rel, results);
        }

        foreach (var file in Directory.EnumerateFiles(root))
        {
            var name = Path.GetFileName(file);
            var rel = relativePrefix.Length == 0 ? name : relativePrefix + "/" + name;
            var info = new FileInfo(file);
            results.Add(new WalkEntry { FullPath = file, RelativePath = rel, IsDirectory = false, SizeBytes = info.Length, Attributes = info.Attributes, ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks });
        }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        var dir = Path.GetDirectoryName(path)!;
        var nameNoExt = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{nameNoExt} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// Best-effort: overwrites each file's bytes with zeros before deleting. This is NOT a guarantee
    /// against forensic recovery on an SSD - wear-leveling and TRIM mean the drive's flash controller
    /// can retain the original physical cells beyond the OS's control. Full-disk encryption
    /// (BitLocker) is the real mitigation for that; this only stops the easy case (undelete tools,
    /// raw NTFS parsing of an unencrypted volume).
    private static void WipeDirectoryBestEffort(string folderPath)
    {
        foreach (var file in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
        {
            try
            {
                var length = new FileInfo(file).Length;
                if (length > 0)
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None);
                    var zero = new byte[Math.Min(length, 1024 * 1024)];
                    long written = 0;
                    while (written < length)
                    {
                        var take = (int)Math.Min(zero.Length, length - written);
                        fs.Write(zero, 0, take);
                        written += take;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogWarning("EncryptedFolderService.WipeDirectoryBestEffort", ex);
            }
        }

        Directory.Delete(folderPath, recursive: true);
    }
}
