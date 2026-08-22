using FileExplorer.Services;

namespace FileExplorer.Tests;

public class FileOperationServiceTests
{
    [Fact]
    public void DetermineDropOperation_SameDrive_IsMove()
    {
        var op = FileOperationService.DetermineDropOperation(new[] { @"C:\a\file.txt" }, @"C:\b", forceMove: false);

        Assert.Equal(FileDropOperation.Move, op);
    }

    [Fact]
    public void DetermineDropOperation_DifferentDrive_IsCopy()
    {
        var op = FileOperationService.DetermineDropOperation(new[] { @"C:\a\file.txt" }, @"D:\b", forceMove: false);

        Assert.Equal(FileDropOperation.Copy, op);
    }

    [Fact]
    public void DetermineDropOperation_DifferentDriveWithForceMove_IsMove()
    {
        var op = FileOperationService.DetermineDropOperation(new[] { @"C:\a\file.txt" }, @"D:\b", forceMove: true);

        Assert.Equal(FileDropOperation.Move, op);
    }

    [Fact]
    public void DropCaption_Move_UsesMovePrefix()
    {
        Assert.Equal("Move to Target", FileOperationService.DropCaption(FileDropOperation.Move, @"C:\Folder\Target"));
    }

    [Fact]
    public void DropCaption_Copy_UsesCopyPrefix()
    {
        Assert.Equal("Copy to Target", FileOperationService.DropCaption(FileDropOperation.Copy, @"C:\Folder\Target\"));
    }

    [Fact]
    public void SameDrive_SamePathRoot_ReturnsTrue()
    {
        Assert.True(FileOperationService.SameDrive(@"C:\Users\me\a", @"C:\Users\me\b"));
    }

    [Fact]
    public void SameDrive_DifferentDriveLetters_ReturnsFalse()
    {
        Assert.False(FileOperationService.SameDrive(@"C:\Users\me\a", @"D:\Backup\b"));
    }

    [Fact]
    public void SameDrive_EitherSideRemote_ReturnsFalse()
    {
        Assert.False(FileOperationService.SameDrive("sftp://abc123/home", @"C:\Users\me"));
    }

    [Fact]
    public void IsValidDropTarget_RemoteInvolved_AlwaysTrue()
    {
        Assert.True(FileOperationService.IsValidDropTarget(new[] { "sftp://abc123/home/file.txt" }, @"C:\Target"));
    }

    [Fact]
    public void IsValidDropTarget_DroppingOntoOwnParent_ReturnsFalse()
    {
        var tempDir = Path.GetTempPath();
        var file = Path.Combine(tempDir, "somefile.txt");

        Assert.False(FileOperationService.IsValidDropTarget(new[] { file }, tempDir.TrimEnd(Path.DirectorySeparatorChar)));
    }

    [Fact]
    public void IsValidDropTarget_DroppingOntoSelf_ReturnsFalse()
    {
        var path = Path.Combine(Path.GetTempPath(), "self");

        Assert.False(FileOperationService.IsValidDropTarget(new[] { path }, path));
    }

    [Fact]
    public void IsValidDropTarget_UnrelatedTarget_ReturnsTrue()
    {
        var source = Path.Combine(Path.GetTempPath(), "source-file.txt");
        var target = Path.Combine(Path.GetTempPath(), "unrelated-folder");

        Assert.True(FileOperationService.IsValidDropTarget(new[] { source }, target));
    }

    [Fact]
    public void MakeUniqueDestination_PathDoesNotExist_ReturnsAsIs()
    {
        var candidate = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.txt");

        Assert.Equal(candidate, FileOperationService.MakeUniqueDestination(candidate));
    }

    [Fact]
    public void MakeUniqueDestination_FileExists_AppendsCounter()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"unique-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var existing = Path.Combine(dir, "report.txt");
            File.WriteAllText(existing, "x");

            var result = FileOperationService.MakeUniqueDestination(existing);

            Assert.Equal(Path.Combine(dir, "report (2).txt"), result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MakeUniqueDestination_SkipsAlreadyTakenCounters()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"unique-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "report.txt"), "x");
            File.WriteAllText(Path.Combine(dir, "report (2).txt"), "x");

            var result = FileOperationService.MakeUniqueDestination(Path.Combine(dir, "report.txt"));

            Assert.Equal(Path.Combine(dir, "report (3).txt"), result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MakeUniqueDestination_FolderExists_AppendsCounter()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"unique-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var existingFolder = Path.Combine(dir, "New folder");
            Directory.CreateDirectory(existingFolder);

            var result = FileOperationService.MakeUniqueDestination(existingFolder);

            Assert.Equal(Path.Combine(dir, "New folder (2)"), result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
