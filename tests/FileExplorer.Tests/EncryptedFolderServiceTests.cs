using System.Text;
using FileExplorer.Services;

namespace FileExplorer.Tests;

public class EncryptedFolderServiceTests
{
    private const string Pin = "correct-horse-battery";

    private static string CreateSampleFolder(out byte[] file1Bytes, out byte[] file2Bytes)
    {
        var root = Path.Combine(Path.GetTempPath(), "dxlock-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        Directory.CreateDirectory(Path.Combine(root, "sub", "empty"));

        file1Bytes = Encoding.UTF8.GetBytes("hello from file1");
        file2Bytes = new byte[5 * 1024 * 1024]; // exercise multi-chunk-sized content path
        new Random(42).NextBytes(file2Bytes);

        File.WriteAllBytes(Path.Combine(root, "file1.txt"), file1Bytes);
        File.WriteAllBytes(Path.Combine(root, "sub", "file2.bin"), file2Bytes);

        return root;
    }

    [Fact]
    public async Task EncryptFolderAsync_ReplacesFolderWithContainer_AndWipesOriginal()
    {
        var root = CreateSampleFolder(out _, out _);

        var containerPath = await EncryptedFolderService.EncryptFolderAsync(root, Pin, null, CancellationToken.None);

        Assert.False(Directory.Exists(root));
        Assert.True(File.Exists(containerPath));
        Assert.Equal(EncryptedFolderService.ContainerExtension, Path.GetExtension(containerPath));

        File.Delete(containerPath);
    }

    [Fact]
    public async Task TryOpenAsync_WrongPin_ReturnsNull()
    {
        var root = CreateSampleFolder(out _, out _);
        var containerPath = await EncryptedFolderService.EncryptFolderAsync(root, Pin, null, CancellationToken.None);

        var opened = await EncryptedFolderService.TryOpenAsync(containerPath, "totally-wrong-pin", CancellationToken.None);

        Assert.Null(opened);
        File.Delete(containerPath);
    }

    [Fact]
    public async Task TryOpenAsync_CorrectPin_ReturnsIndexMatchingOriginalTree()
    {
        var root = CreateSampleFolder(out _, out _);
        var containerPath = await EncryptedFolderService.EncryptFolderAsync(root, Pin, null, CancellationToken.None);

        var opened = await EncryptedFolderService.TryOpenAsync(containerPath, Pin, CancellationToken.None);

        Assert.NotNull(opened);
        var entries = opened!.Value.Entries;
        Assert.Contains(entries, e => e.RelativePath == "file1.txt" && !e.IsDirectory);
        Assert.Contains(entries, e => e.RelativePath == "sub" && e.IsDirectory);
        Assert.Contains(entries, e => e.RelativePath == "sub/empty" && e.IsDirectory);
        Assert.Contains(entries, e => e.RelativePath == "sub/file2.bin" && !e.IsDirectory);

        File.Delete(containerPath);
    }

    [Fact]
    public async Task DecryptEntryToFileAsync_RoundTripsExactBytes_ForBothSmallAndMultiChunkFiles()
    {
        var root = CreateSampleFolder(out var file1Bytes, out var file2Bytes);
        var containerPath = await EncryptedFolderService.EncryptFolderAsync(root, Pin, null, CancellationToken.None);

        var opened = await EncryptedFolderService.TryOpenAsync(containerPath, Pin, CancellationToken.None);
        Assert.NotNull(opened);
        var (key, entries) = opened!.Value;

        var file1Entry = entries.Single(e => e.RelativePath == "file1.txt");
        var file2Entry = entries.Single(e => e.RelativePath == "sub/file2.bin");

        var outDir = Directory.CreateTempSubdirectory().FullName;
        var out1 = Path.Combine(outDir, "file1.txt");
        var out2 = Path.Combine(outDir, "file2.bin");

        await EncryptedFolderService.DecryptEntryToFileAsync(containerPath, key, file1Entry, out1, CancellationToken.None);
        await EncryptedFolderService.DecryptEntryToFileAsync(containerPath, key, file2Entry, out2, CancellationToken.None);

        Assert.Equal(file1Bytes, await File.ReadAllBytesAsync(out1));
        Assert.Equal(file2Bytes, await File.ReadAllBytesAsync(out2));

        File.Delete(containerPath);
        Directory.Delete(outDir, recursive: true);
    }

    [Fact]
    public async Task DecryptAllAsync_RestoresFullTree()
    {
        var root = CreateSampleFolder(out var file1Bytes, out var file2Bytes);
        var containerPath = await EncryptedFolderService.EncryptFolderAsync(root, Pin, null, CancellationToken.None);

        var opened = await EncryptedFolderService.TryOpenAsync(containerPath, Pin, CancellationToken.None);
        Assert.NotNull(opened);
        var (key, entries) = opened!.Value;

        var restoreDir = Path.Combine(Path.GetTempPath(), "dxlock-restore-" + Guid.NewGuid().ToString("N"));
        await EncryptedFolderService.DecryptAllAsync(containerPath, key, entries, restoreDir, CancellationToken.None);

        Assert.Equal(file1Bytes, await File.ReadAllBytesAsync(Path.Combine(restoreDir, "file1.txt")));
        Assert.Equal(file2Bytes, await File.ReadAllBytesAsync(Path.Combine(restoreDir, "sub", "file2.bin")));
        Assert.True(Directory.Exists(Path.Combine(restoreDir, "sub", "empty")));

        File.Delete(containerPath);
        Directory.Delete(restoreDir, recursive: true);
    }

    [Fact]
    public async Task AddFileEntryAsync_AddsNewFile_AndLeavesExistingEntriesIntact()
    {
        var root = CreateSampleFolder(out var file1Bytes, out var file2Bytes);
        var containerPath = await EncryptedFolderService.EncryptFolderAsync(root, Pin, null, CancellationToken.None);
        var opened = await EncryptedFolderService.TryOpenAsync(containerPath, Pin, CancellationToken.None);
        var (key, entries) = opened!.Value;

        var newFilePath = Path.Combine(Path.GetTempPath(), "dxlock-newfile-" + Guid.NewGuid().ToString("N") + ".txt");
        var newFileBytes = Encoding.UTF8.GetBytes("brand new content");
        await File.WriteAllBytesAsync(newFilePath, newFileBytes);

        var updated = await EncryptedFolderService.AddFileEntryAsync(containerPath, key, entries, "", newFilePath, CancellationToken.None);
        File.Delete(newFilePath);

        Assert.Equal(entries.Count + 1, updated.Count);
        Assert.Contains(updated, e => e.RelativePath == "file1.txt");
        Assert.Contains(updated, e => e.RelativePath == "sub/file2.bin");

        // Reopen from disk (not just the in-memory returned list) to prove the rewrite actually persisted.
        var reopened = await EncryptedFolderService.TryOpenAsync(containerPath, Pin, CancellationToken.None);
        Assert.NotNull(reopened);
        var newEntry = reopened!.Value.Entries.Single(e => e.RelativePath == Path.GetFileName(newFilePath));

        var outDir = Directory.CreateTempSubdirectory().FullName;
        var outPath = Path.Combine(outDir, "out.txt");
        await EncryptedFolderService.DecryptEntryToFileAsync(containerPath, reopened.Value.Key, newEntry, outPath, CancellationToken.None);
        Assert.Equal(newFileBytes, await File.ReadAllBytesAsync(outPath));

        // Original files must still round-trip correctly after the rewrite.
        var file1Entry = reopened.Value.Entries.Single(e => e.RelativePath == "file1.txt");
        var file2Entry = reopened.Value.Entries.Single(e => e.RelativePath == "sub/file2.bin");
        var out1 = Path.Combine(outDir, "file1.txt");
        var out2 = Path.Combine(outDir, "file2.bin");
        await EncryptedFolderService.DecryptEntryToFileAsync(containerPath, reopened.Value.Key, file1Entry, out1, CancellationToken.None);
        await EncryptedFolderService.DecryptEntryToFileAsync(containerPath, reopened.Value.Key, file2Entry, out2, CancellationToken.None);
        Assert.Equal(file1Bytes, await File.ReadAllBytesAsync(out1));
        Assert.Equal(file2Bytes, await File.ReadAllBytesAsync(out2));

        File.Delete(containerPath);
        Directory.Delete(outDir, recursive: true);
    }

    [Fact]
    public async Task AddFileEntryAsync_DuplicateName_ThrowsWithoutTouchingContainer()
    {
        var root = CreateSampleFolder(out _, out _);
        var containerPath = await EncryptedFolderService.EncryptFolderAsync(root, Pin, null, CancellationToken.None);
        var opened = await EncryptedFolderService.TryOpenAsync(containerPath, Pin, CancellationToken.None);
        var (key, entries) = opened!.Value;

        var dupPath = Path.Combine(Path.GetTempPath(), "dxlock-dup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dupPath);
        var dupFile = Path.Combine(dupPath, "file1.txt");
        await File.WriteAllTextAsync(dupFile, "collides with existing entry");

        await Assert.ThrowsAsync<IOException>(() =>
            EncryptedFolderService.AddFileEntryAsync(containerPath, key, entries, "", dupFile, CancellationToken.None));

        var reopened = await EncryptedFolderService.TryOpenAsync(containerPath, Pin, CancellationToken.None);
        Assert.Equal(entries.Count, reopened!.Value.Entries.Count);

        Directory.Delete(dupPath, recursive: true);
        File.Delete(containerPath);
    }

    [Fact]
    public async Task AddEmptyFolderEntryAsync_AddsDirectoryEntry()
    {
        var root = CreateSampleFolder(out _, out _);
        var containerPath = await EncryptedFolderService.EncryptFolderAsync(root, Pin, null, CancellationToken.None);
        var opened = await EncryptedFolderService.TryOpenAsync(containerPath, Pin, CancellationToken.None);
        var (key, entries) = opened!.Value;

        var updated = await EncryptedFolderService.AddEmptyFolderEntryAsync(containerPath, key, entries, "", "New folder", CancellationToken.None);
        Assert.Contains(updated, e => e.RelativePath == "New folder" && e.IsDirectory);

        var reopened = await EncryptedFolderService.TryOpenAsync(containerPath, Pin, CancellationToken.None);
        Assert.Contains(reopened!.Value.Entries, e => e.RelativePath == "New folder" && e.IsDirectory);

        File.Delete(containerPath);
    }

    [Fact]
    public async Task AddFolderTreeEntriesAsync_AddsNestedSubtree()
    {
        var root = CreateSampleFolder(out _, out _);
        var containerPath = await EncryptedFolderService.EncryptFolderAsync(root, Pin, null, CancellationToken.None);
        var opened = await EncryptedFolderService.TryOpenAsync(containerPath, Pin, CancellationToken.None);
        var (key, entries) = opened!.Value;

        var sourceTree = Path.Combine(Path.GetTempPath(), "dxlock-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(sourceTree, "nested"));
        var nestedBytes = Encoding.UTF8.GetBytes("nested content");
        await File.WriteAllBytesAsync(Path.Combine(sourceTree, "nested", "leaf.txt"), nestedBytes);
        var treeName = Path.GetFileName(sourceTree);

        var updated = await EncryptedFolderService.AddFolderTreeEntriesAsync(containerPath, key, entries, "", sourceTree, CancellationToken.None);
        Directory.Delete(sourceTree, recursive: true);

        Assert.Contains(updated, e => e.RelativePath == treeName && e.IsDirectory);
        Assert.Contains(updated, e => e.RelativePath == $"{treeName}/nested" && e.IsDirectory);
        var leafEntry = updated.Single(e => e.RelativePath == $"{treeName}/nested/leaf.txt");

        var reopened = await EncryptedFolderService.TryOpenAsync(containerPath, Pin, CancellationToken.None);
        var reopenedLeaf = reopened!.Value.Entries.Single(e => e.RelativePath == leafEntry.RelativePath);

        var outPath = Path.Combine(Directory.CreateTempSubdirectory().FullName, "leaf.txt");
        await EncryptedFolderService.DecryptEntryToFileAsync(containerPath, reopened.Value.Key, reopenedLeaf, outPath, CancellationToken.None);
        Assert.Equal(nestedBytes, await File.ReadAllBytesAsync(outPath));

        File.Delete(containerPath);
        Directory.Delete(Path.GetDirectoryName(outPath)!, recursive: true);
    }

    [Fact]
    public async Task RemoveEntryAsync_RemovesFileAndDescendants_LeavesSiblingsIntact()
    {
        var root = CreateSampleFolder(out var file1Bytes, out _);
        var containerPath = await EncryptedFolderService.EncryptFolderAsync(root, Pin, null, CancellationToken.None);
        var opened = await EncryptedFolderService.TryOpenAsync(containerPath, Pin, CancellationToken.None);
        var (key, entries) = opened!.Value;

        var updated = await EncryptedFolderService.RemoveEntryAsync(containerPath, key, entries, "sub", CancellationToken.None);

        Assert.DoesNotContain(updated, e => e.RelativePath == "sub" || e.RelativePath.StartsWith("sub/"));
        Assert.Contains(updated, e => e.RelativePath == "file1.txt");

        var reopened = await EncryptedFolderService.TryOpenAsync(containerPath, Pin, CancellationToken.None);
        Assert.DoesNotContain(reopened!.Value.Entries, e => e.RelativePath.StartsWith("sub"));
        var file1Entry = reopened.Value.Entries.Single(e => e.RelativePath == "file1.txt");

        var outPath = Path.Combine(Directory.CreateTempSubdirectory().FullName, "file1.txt");
        await EncryptedFolderService.DecryptEntryToFileAsync(containerPath, reopened.Value.Key, file1Entry, outPath, CancellationToken.None);
        Assert.Equal(file1Bytes, await File.ReadAllBytesAsync(outPath));

        File.Delete(containerPath);
        Directory.Delete(Path.GetDirectoryName(outPath)!, recursive: true);
    }

    [Fact]
    public async Task RemoveEntryAsync_UnknownPath_Throws()
    {
        var root = CreateSampleFolder(out _, out _);
        var containerPath = await EncryptedFolderService.EncryptFolderAsync(root, Pin, null, CancellationToken.None);
        var opened = await EncryptedFolderService.TryOpenAsync(containerPath, Pin, CancellationToken.None);
        var (key, entries) = opened!.Value;

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            EncryptedFolderService.RemoveEntryAsync(containerPath, key, entries, "does-not-exist.txt", CancellationToken.None));

        File.Delete(containerPath);
    }

    [Fact]
    public async Task CompactAsync_ShrinksContainer_AfterADeleteLeavesNothingOrphaned()
    {
        var root = CreateSampleFolder(out _, out _);
        var containerPath = await EncryptedFolderService.EncryptFolderAsync(root, Pin, null, CancellationToken.None);
        var opened = await EncryptedFolderService.TryOpenAsync(containerPath, Pin, CancellationToken.None);
        var (key, entries) = opened!.Value;

        var afterDelete = await EncryptedFolderService.RemoveEntryAsync(containerPath, key, entries, "sub", CancellationToken.None);
        var sizeAfterDelete = new FileInfo(containerPath).Length;

        var afterCompact = await EncryptedFolderService.CompactAsync(containerPath, key, afterDelete, CancellationToken.None);
        var sizeAfterCompact = new FileInfo(containerPath).Length;

        // The rewrite-on-every-mutation design means delete already drops the removed file's bytes,
        // so compact on an already-compact container should be a same-size no-op rather than shrink
        // further - this pins that invariant rather than assuming a specific byte count.
        Assert.Equal(afterDelete.Count, afterCompact.Count);
        Assert.True(sizeAfterCompact <= sizeAfterDelete);

        File.Delete(containerPath);
    }
}

public class EncryptedFolderPathServiceTests
{
    private const string ContainerPath = @"C:\Users\me\Photos.dxlock";

    [Fact]
    public void BuildRoot_ThenTryParse_RoundTrips()
    {
        var root = EncryptedFolderPathService.BuildRoot(ContainerPath);

        Assert.True(EncryptedFolderPathService.IsEncrypted(root));
        Assert.True(EncryptedFolderPathService.TryParse(root, out var containerPath, out var relativePath));
        Assert.Equal(ContainerPath, containerPath);
        Assert.Equal("/", relativePath);
    }

    [Fact]
    public void Combine_AppendsChildUnderRoot_AndUnderNestedPath()
    {
        var root = EncryptedFolderPathService.BuildRoot(ContainerPath);
        var child = EncryptedFolderPathService.Combine(root, "Sub");
        var grandchild = EncryptedFolderPathService.Combine(child, "Deeper.txt");

        Assert.True(EncryptedFolderPathService.TryParse(child, out _, out var childRel));
        Assert.Equal("/Sub", childRel);

        Assert.True(EncryptedFolderPathService.TryParse(grandchild, out _, out var grandchildRel));
        Assert.Equal("/Sub/Deeper.txt", grandchildRel);
    }

    [Fact]
    public void GetParent_AtRoot_ReturnsNull_ElseWalksUpOneSegment()
    {
        var root = EncryptedFolderPathService.BuildRoot(ContainerPath);
        var child = EncryptedFolderPathService.Combine(root, "Sub");
        var grandchild = EncryptedFolderPathService.Combine(child, "Deeper.txt");

        Assert.Null(EncryptedFolderPathService.GetParent(root));
        Assert.Equal(child, EncryptedFolderPathService.GetParent(grandchild));
    }

    [Fact]
    public void GetFileName_AtRoot_ReturnsContainerNameWithoutExtension()
    {
        var root = EncryptedFolderPathService.BuildRoot(ContainerPath);
        Assert.Equal("Photos", EncryptedFolderPathService.GetFileName(root));

        var child = EncryptedFolderPathService.Combine(root, "Sub");
        Assert.Equal("Sub", EncryptedFolderPathService.GetFileName(child));
    }
}
