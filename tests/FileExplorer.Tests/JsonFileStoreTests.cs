using FileExplorer.Services;

namespace FileExplorer.Tests;

// Each store uses a randomized file name under the real app-data folder (JsonFileStore hardcodes
// "FileExplorerApp" as the directory) so a test run never collides with - or clobbers - a real
// user's actual favourites.json/settings.json/etc, and each test cleans its own file up afterward.
public class JsonFileStoreTests : IDisposable
{
    private readonly List<string> _createdFileNames = new();

    private JsonFileStore<T> CreateStore<T>(Func<T> createDefault)
    {
        var fileName = $"test-{Guid.NewGuid():N}.json";
        _createdFileNames.Add(fileName);
        return new JsonFileStore<T>(fileName, createDefault);
    }

    public void Dispose()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorerApp");

        foreach (var fileName in _createdFileNames)
        {
            var path = Path.Combine(dir, fileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Load_NoFileYet_ReturnsDefault()
    {
        var store = CreateStore(() => new List<string> { "seed" });

        Assert.Equal(new List<string> { "seed" }, store.Load());
    }

    [Fact]
    public void Load_IsCached_ReturnsSameInstanceOnRepeatCalls()
    {
        var store = CreateStore(() => new List<string>());

        var first = store.Load();
        var second = store.Load();

        Assert.Same(first, second);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsThroughDisk()
    {
        var writer = CreateStore(() => new List<string>());
        writer.Save(new List<string> { "a", "b", "c" });

        // A fresh store instance pointed at the same file name proves the value was actually
        // persisted to disk, not just cached in memory by the writer instance.
        var fileName = _createdFileNames[^1];
        var reader = new JsonFileStore<List<string>>(fileName, () => new List<string>());

        Assert.Equal(new List<string> { "a", "b", "c" }, reader.Load());
    }

    [Fact]
    public void Save_UpdatesCacheImmediately_WithoutRereadingDisk()
    {
        var store = CreateStore(() => new List<string>());
        store.Load();

        store.Save(new List<string> { "updated" });

        Assert.Equal(new List<string> { "updated" }, store.Load());
    }

    private sealed record Settings(int Volume, bool Muted);

    [Fact]
    public void Load_SingleObjectStore_RoundTrips()
    {
        var writer = CreateStore(() => new Settings(50, false));
        writer.Save(new Settings(80, true));

        var fileName = _createdFileNames[^1];
        var reader = new JsonFileStore<Settings>(fileName, () => new Settings(50, false));

        Assert.Equal(new Settings(80, true), reader.Load());
    }
}
