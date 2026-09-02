using FileExplorer.Models;
using FileExplorer.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FileExplorer.Tests;

public sealed class ImageConversionServiceTests : IDisposable
{
    private readonly string _dir;

    public ImageConversionServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "convtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private string MakePng(string relativePath, int w = 8, int h = 8)
    {
        var full = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var image = new Image<Rgba32>(w, h);
        image.Save(full);
        return full;
    }

    private static ConversionFormat Jpeg => ImageConversionService.Targets.Single(t => t.Extension == ".jpg");
    private static ConversionFormat Webp => ImageConversionService.Targets.Single(t => t.Extension == ".webp");

    [Fact]
    public async Task Converts_a_file_and_keeps_the_original()
    {
        var src = MakePng("photo.png");
        var options = new ConversionOptions(Jpeg, FolderScanDepth.DirectChildrenOnly, PostConversionAction.KeepOriginal, 85);

        var outcome = await ImageConversionService.ConvertAsync(src, options, CancellationToken.None);

        Assert.Equal(ConversionStatus.Converted, outcome.Status);
        Assert.True(File.Exists(src), "original kept");
        Assert.True(File.Exists(Path.ChangeExtension(src, ".jpg")), "jpg written");

        using var jpg = await Image.LoadAsync(outcome.DestinationPath!);
        Assert.Equal(8, jpg.Width);
    }

    [Fact]
    public async Task Delete_action_recycles_the_original()
    {
        var recycled = new List<string>();
        var previous = ImageConversionService.RecycleFile;
        ImageConversionService.RecycleFile = p => { recycled.Add(p); File.Delete(p); };
        try
        {
            var src = MakePng("x.png");
            var options = new ConversionOptions(Webp, FolderScanDepth.DirectChildrenOnly, PostConversionAction.DeleteOriginal, 80);

            await ImageConversionService.ConvertAsync(src, options, CancellationToken.None);

            Assert.Equal(new[] { src }, recycled);
            Assert.False(File.Exists(src));
            Assert.True(File.Exists(Path.ChangeExtension(src, ".webp")));
        }
        finally
        {
            ImageConversionService.RecycleFile = previous;
        }
    }

    [Fact]
    public async Task MoveToOriginals_puts_the_source_in_an_Originals_subfolder()
    {
        var src = MakePng("sub/pic.png");
        var options = new ConversionOptions(Jpeg, FolderScanDepth.DirectChildrenOnly, PostConversionAction.MoveToOriginals, 85);

        await ImageConversionService.ConvertAsync(src, options, CancellationToken.None);

        Assert.False(File.Exists(src));
        Assert.True(File.Exists(Path.Combine(_dir, "sub", "Originals", "pic.png")));
        Assert.True(File.Exists(Path.Combine(_dir, "sub", "pic.jpg")));
    }

    [Fact]
    public async Task Same_target_format_is_skipped()
    {
        var src = MakePng("already.png");
        var options = new ConversionOptions(
            ImageConversionService.Targets.Single(t => t.Extension == ".png"),
            FolderScanDepth.DirectChildrenOnly, PostConversionAction.DeleteOriginal, 85);

        var outcome = await ImageConversionService.ConvertAsync(src, options, CancellationToken.None);

        Assert.Equal(ConversionStatus.Skipped, outcome.Status);
        Assert.True(File.Exists(src), "a skipped file is never touched");
    }

    [Fact]
    public void ResolveSources_respects_folder_depth()
    {
        MakePng("top.png");
        MakePng("a/nested.png");
        MakePng("a/b/deep.png");
        MakePng("notes.txt".Replace(".txt", ".png")); // another top-level image

        var direct = ImageConversionService.ResolveSources(new[] { _dir }, FolderScanDepth.DirectChildrenOnly);
        var recurse = ImageConversionService.ResolveSources(new[] { _dir }, FolderScanDepth.Recurse);

        Assert.Equal(2, direct.Count);
        Assert.Equal(4, recurse.Count);
    }

    [Fact]
    public void ResolveSources_dedupes_a_file_also_covered_by_a_selected_folder()
    {
        var file = MakePng("dup.png");
        var resolved = ImageConversionService.ResolveSources(new[] { _dir, file }, FolderScanDepth.DirectChildrenOnly);
        Assert.Single(resolved);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
