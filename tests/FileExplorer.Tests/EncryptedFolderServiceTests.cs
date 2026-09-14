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
