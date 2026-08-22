using FileExplorer.Models;
using FileExplorer.Services;

namespace FileExplorer.Tests;

public class RemotePathServiceTests
{
    [Theory]
    [InlineData("sftp://abc123/home/user", true)]
    [InlineData("ftp://abc123/pub", true)]
    [InlineData("ftps://abc123/pub", true)]
    [InlineData(@"C:\Users\me", false)]
    [InlineData("", false)]
    public void IsRemote_DetectsScheme(string path, bool expected)
    {
        Assert.Equal(expected, RemotePathService.IsRemote(path));
    }

    [Fact]
    public void TryParse_SplitsSchemeConnectionAndPath()
    {
        var ok = RemotePathService.TryParse("sftp://abc123/home/user/docs", out var scheme, out var connectionId, out var remotePath);

        Assert.True(ok);
        Assert.Equal("sftp", scheme);
        Assert.Equal("abc123", connectionId);
        Assert.Equal("/home/user/docs", remotePath);
    }

    [Fact]
    public void TryParse_RootWithNoTrailingSlash_ReturnsRootPath()
    {
        var ok = RemotePathService.TryParse("ftp://abc123", out _, out var connectionId, out var remotePath);

        Assert.True(ok);
        Assert.Equal("abc123", connectionId);
        Assert.Equal("/", remotePath);
    }

    [Fact]
    public void TryParse_LocalPath_ReturnsFalse()
    {
        var ok = RemotePathService.TryParse(@"C:\Users\me", out var scheme, out var connectionId, out var remotePath);

        Assert.False(ok);
        Assert.Equal(string.Empty, scheme);
        Assert.Equal(string.Empty, connectionId);
        Assert.Equal(string.Empty, remotePath);
    }

    [Fact]
    public void BuildRoot_FormatsSchemeAndConnection()
    {
        Assert.Equal("sftp://abc123/", RemotePathService.BuildRoot("sftp", "abc123"));
    }

    [Fact]
    public void GetParent_AtRoot_ReturnsNull()
    {
        Assert.Null(RemotePathService.GetParent("sftp://abc123/"));
    }

    [Fact]
    public void GetParent_OneLevelDeep_ReturnsRoot()
    {
        Assert.Equal("sftp://abc123/", RemotePathService.GetParent("sftp://abc123/home"));
    }

    [Fact]
    public void GetParent_MultipleLevelsDeep_TrimsLastSegment()
    {
        Assert.Equal("sftp://abc123/home", RemotePathService.GetParent("sftp://abc123/home/docs"));
    }

    [Fact]
    public void GetFileName_ReturnsLastSegment()
    {
        Assert.Equal("docs", RemotePathService.GetFileName("sftp://abc123/home/docs"));
    }

    [Fact]
    public void GetFileName_AtRoot_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, RemotePathService.GetFileName("sftp://abc123/"));
    }

    [Fact]
    public void Combine_AppendsChildToRemotePath()
    {
        Assert.Equal("sftp://abc123/home/newfolder", RemotePathService.Combine("sftp://abc123/home", "newfolder"));
    }

    [Fact]
    public void Combine_LocalPath_UsesPathCombine()
    {
        Assert.Equal(Path.Combine(@"C:\Users\me", "docs"), RemotePathService.Combine(@"C:\Users\me", "docs"));
    }

    [Fact]
    public void GetBreadcrumbSegments_BuildsFullPathPerSegment()
    {
        var segments = RemotePathService.GetBreadcrumbSegments("sftp://abc123/home/user/docs");

        Assert.Equal(3, segments.Count);
        Assert.Equal(("home", "sftp://abc123/home"), segments[0]);
        Assert.Equal(("user", "sftp://abc123/home/user"), segments[1]);
        Assert.Equal(("docs", "sftp://abc123/home/user/docs"), segments[2]);
    }

    [Fact]
    public void GetBreadcrumbSegments_LocalPath_ReturnsEmpty()
    {
        Assert.Empty(RemotePathService.GetBreadcrumbSegments(@"C:\Users\me"));
    }

    [Theory]
    [InlineData(RemoteProtocol.Ftp, "ftp")]
    [InlineData(RemoteProtocol.Ftps, "ftps")]
    [InlineData(RemoteProtocol.Sftp, "sftp")]
    public void SchemeFor_MapsProtocolToScheme(RemoteProtocol protocol, string expectedScheme)
    {
        Assert.Equal(expectedScheme, RemotePathService.SchemeFor(protocol));
    }
}
