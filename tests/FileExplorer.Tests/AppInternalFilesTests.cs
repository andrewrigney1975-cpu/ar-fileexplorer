using FileExplorer.Helpers;

namespace FileExplorer.Tests;

public sealed class AppInternalFilesTests
{
    [Theory]
    [InlineData(@"C:\Photos\.docket-thumbs.cache", true)]
    [InlineData(@"C:\Photos\.arexx-thumbs.cache", true)]
    [InlineData(@"C:\.docket-benchmark-abc123.tmp", true)]
    [InlineData(@"C:\.arexx-benchmark-deadbeef.tmp", true)]
    [InlineData(@"C:\Photos\holiday.jpg", false)]
    [InlineData(@"C:\Photos\thumbs.db", false)]
    [InlineData(@"C:\Photos\my.docket-thumbs.cache.txt", false)]
    [InlineData(@"C:\Photos\.docket-thumbs.cache.bak", false)]
    public void IsInternal_matches_only_the_app_written_files(string path, bool expected) =>
        Assert.Equal(expected, AppInternalFiles.IsInternal(path));

    [Fact]
    public void Match_is_case_insensitive() =>
        Assert.True(AppInternalFiles.IsInternal(@"C:\X\.DOCKET-THUMBS.CACHE"));
}
