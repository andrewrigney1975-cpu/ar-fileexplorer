using FileExplorer.Models;
using FileExplorer.Services;

namespace FileExplorer.WinUI.Tests;

public class FileSystemItemTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1024L * 1024, "1 MB")]
    [InlineData(1024L * 1024 * 1024, "1 GB")]
    [InlineData(1024L * 1024 * 1024 * 1024, "1 TB")]
    public void FormatSize_FormatsWithAppropriateUnit(long bytes, string expected)
    {
        Assert.Equal(expected, FileSystemItem.FormatSize(bytes));
    }

    [Fact]
    public void FormatAttributes_CombinesFlagLettersInFixedOrder()
    {
        var attrs = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.Archive;

        Assert.Equal("RHA", FileSystemItem.FormatAttributes(attrs));
    }

    [Fact]
    public void FormatAttributes_NoFlags_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, FileSystemItem.FormatAttributes(FileAttributes.Normal));
    }

    [Fact]
    public void FormatAttributes_ReparsePoint_IncludesLinkLetter()
    {
        Assert.Contains("L", FileSystemItem.FormatAttributes(FileAttributes.ReparsePoint));
    }

    [Fact]
    public void IsRemote_LocalPath_IsFalse()
    {
        var item = new FileSystemItem { Name = "file.txt", FullPath = @"C:\Users\me\file.txt", IsDirectory = false };

        Assert.False(item.IsRemote);
    }

    [Fact]
    public void IsRemote_SftpPath_IsTrue()
    {
        var item = new FileSystemItem { Name = "file.txt", FullPath = "sftp://abc123/home/file.txt", IsDirectory = false };

        Assert.True(item.IsRemote);
    }

    [Fact]
    public void IsLink_DefaultsToFalse()
    {
        var item = new FileSystemItem { Name = "folder", FullPath = @"C:\folder", IsDirectory = true };

        Assert.False(item.IsLink);
        Assert.Null(item.LinkGlyph);
    }

    [Fact]
    public void IsLink_TrueWhenLinkKindSet()
    {
        var item = new FileSystemItem
        {
            Name = "shortcut",
            FullPath = @"C:\shortcut",
            IsDirectory = true,
            LinkKind = ReparsePointKind.SymbolicLink,
        };

        Assert.True(item.IsLink);
        Assert.NotNull(item.LinkGlyph);
    }

    [Fact]
    public void Kind_Directory_IsFileFolder()
    {
        var item = new FileSystemItem { Name = "folder", FullPath = @"C:\folder", IsDirectory = true };

        Assert.Equal("File folder", item.Kind);
    }

    [Fact]
    public void Kind_FileWithExtension_UppercasesExtension()
    {
        var item = new FileSystemItem { Name = "notes.txt", FullPath = @"C:\notes.txt", IsDirectory = false, Extension = ".txt" };

        Assert.Equal("TXT File", item.Kind);
    }

    [Fact]
    public void Kind_FileNoExtension_IsPlainFile()
    {
        var item = new FileSystemItem { Name = "README", FullPath = @"C:\README", IsDirectory = false, Extension = string.Empty };

        Assert.Equal("File", item.Kind);
    }

    [Fact]
    public void Kind_Junction_ReportsJunction()
    {
        var item = new FileSystemItem
        {
            Name = "link",
            FullPath = @"C:\link",
            IsDirectory = true,
            LinkKind = ReparsePointKind.Junction,
        };

        Assert.Equal("Junction", item.Kind);
    }

    [Fact]
    public void Kind_SymbolicLinkToFolder_DistinguishesFromFileSymlink()
    {
        var folderLink = new FileSystemItem
        {
            Name = "link", FullPath = @"C:\link", IsDirectory = true, LinkKind = ReparsePointKind.SymbolicLink,
        };
        var fileLink = new FileSystemItem
        {
            Name = "link.txt", FullPath = @"C:\link.txt", IsDirectory = false, LinkKind = ReparsePointKind.SymbolicLink,
        };

        Assert.Equal("Symbolic Link (folder)", folderLink.Kind);
        Assert.Equal("Symbolic Link", fileLink.Kind);
    }

    [Fact]
    public void SizeDisplay_Directory_IsEmpty()
    {
        var item = new FileSystemItem { Name = "folder", FullPath = @"C:\folder", IsDirectory = true, SizeBytes = 4096 };

        Assert.Equal(string.Empty, item.SizeDisplay);
    }

    [Fact]
    public void SizeDisplay_File_FormatsSize()
    {
        var item = new FileSystemItem { Name = "file.bin", FullPath = @"C:\file.bin", IsDirectory = false, SizeBytes = 2048 };

        Assert.Equal("2 KB", item.SizeDisplay);
    }
}
