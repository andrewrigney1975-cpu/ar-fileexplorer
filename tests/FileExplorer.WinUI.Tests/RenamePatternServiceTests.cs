using FileExplorer.Models;
using FileExplorer.Services;

namespace FileExplorer.WinUI.Tests;

public class RenamePatternServiceTests
{
    private static FileSystemItem MakeFile(string name) =>
        new() { Name = name, FullPath = @"C:\folder\" + name, IsDirectory = false };

    private static FileSystemItem MakeFolder(string name) =>
        new() { Name = name, FullPath = @"C:\folder\" + name, IsDirectory = true };

    [Fact]
    public void Apply_NamePlaceholder_SubstitutesNameWithoutExtension()
    {
        var item = MakeFile("vacation.jpg");

        Assert.Equal("vacation", RenamePatternService.Apply("{name}", item, 0));
    }

    [Fact]
    public void Apply_NamePlaceholder_Directory_UsesFullNameSinceNoExtension()
    {
        var item = MakeFolder("Photos");

        Assert.Equal("Photos", RenamePatternService.Apply("{name}", item, 0));
    }

    [Fact]
    public void Apply_SequencePlaceholder_UsesOneBasedIndex()
    {
        var item = MakeFile("a.jpg");

        Assert.Equal("1", RenamePatternService.Apply("{n}", item, 0));
        Assert.Equal("5", RenamePatternService.Apply("{n}", item, 4));
    }

    [Fact]
    public void Apply_SequencePlaceholderWithPadding_PadsWithZeros()
    {
        var item = MakeFile("a.jpg");

        Assert.Equal("001", RenamePatternService.Apply("{n:000}", item, 0));
        Assert.Equal("042", RenamePatternService.Apply("{n:000}", item, 41));
    }

    [Fact]
    public void Apply_CombinedPattern_SubstitutesBothPlaceholders()
    {
        var item = MakeFile("vacation.jpg");

        Assert.Equal("Vacation 003", RenamePatternService.Apply("Vacation {n:000}", item, 2));
    }

    [Fact]
    public void Apply_NoPlaceholders_ReturnsPatternUnchanged()
    {
        var item = MakeFile("anything.jpg");

        Assert.Equal("Static Name", RenamePatternService.Apply("Static Name", item, 0));
    }

    [Fact]
    public void Apply_MultipleSequencePlaceholders_BothSubstituted()
    {
        var item = MakeFile("a.jpg");

        Assert.Equal("1 of 1", RenamePatternService.Apply("{n} of {n}", item, 0));
    }
}
