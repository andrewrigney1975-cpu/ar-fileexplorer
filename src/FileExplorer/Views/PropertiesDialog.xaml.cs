using FileExplorer.Models;
using FileExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileExplorer.Views;

/// General-properties panel (Windows Explorer "Properties > General" equivalent), hosted inside a
/// ContentDialog by the caller. Handles both a single selected item and a multi-item selection.
public sealed partial class PropertiesDialog : UserControl
{
    private readonly IReadOnlyList<FileSystemItem> _selection;
    private CancellationTokenSource? _sizeCts;

    public PropertiesDialog(IReadOnlyList<FileSystemItem> selection)
    {
        InitializeComponent();
        _selection = selection;
        Loaded += (_, _) =>
        {
            PopulateGeneral();
            _ = ComputeSizeAsync();
        };
    }

    /// Stops the background size walk if the dialog is closed before it finishes.
    public void CancelSizeComputation() => _sizeCts?.Cancel();

    /// Applies any attribute checkbox changes back to disk. Only meaningful for a single-item
    /// selection - AttributesRow is hidden (and this is a no-op) for multi-select.
    public void ApplyAttributeChanges()
    {
        if (_selection.Count != 1 || AttributesRow.Visibility != Visibility.Visible)
        {
            return;
        }

        try
        {
            var attrs = File.GetAttributes(_selection[0].FullPath);
            attrs = SetFlag(attrs, FileAttributes.ReadOnly, ReadOnlyCheck.IsChecked == true);
            attrs = SetFlag(attrs, FileAttributes.Hidden, HiddenCheck.IsChecked == true);
            File.SetAttributes(_selection[0].FullPath, attrs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static FileAttributes SetFlag(FileAttributes attrs, FileAttributes flag, bool set) =>
        set ? attrs | flag : attrs & ~flag;

    private void PopulateGeneral()
    {
        if (_selection.Count == 1)
        {
            var item = _selection[0];
            IconGlyph.Glyph = item.Glyph;
            NameText.Text = item.Name;
            TypeText.Text = item.Kind;
            LocationText.Text = Path.GetDirectoryName(item.FullPath.TrimEnd(Path.DirectorySeparatorChar)) ?? item.FullPath;

            if (!item.IsDirectory)
            {
                SizeText.Text = FileSystemItem.FormatSize(item.SizeBytes);
            }

            try
            {
                CreatedText.Text = FileSystemItem.FormatDate(File.GetCreationTime(item.FullPath));
                ModifiedText.Text = FileSystemItem.FormatDate(File.GetLastWriteTime(item.FullPath));
                AccessedText.Text = FileSystemItem.FormatDate(File.GetLastAccessTime(item.FullPath));

                var attrs = File.GetAttributes(item.FullPath);
                ReadOnlyCheck.IsChecked = attrs.HasFlag(FileAttributes.ReadOnly);
                HiddenCheck.IsChecked = attrs.HasFlag(FileAttributes.Hidden);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CreatedRow.Height = new GridLength(0);
                ModifiedRow.Height = new GridLength(0);
                AccessedRow.Height = new GridLength(0);
                AttributesRow.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            var files = _selection.Count(i => !i.IsDirectory);
            var folders = _selection.Count(i => i.IsDirectory);

            IconGlyph.Glyph = IconHelper.GenericFile;
            NameText.Text = $"{_selection.Count} items selected";
            TypeText.Text = $"{files} file{(files == 1 ? "" : "s")}, {folders} folder{(folders == 1 ? "" : "s")}";
            LocationText.Text = Path.GetDirectoryName(_selection[0].FullPath.TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty;

            CreatedRow.Height = new GridLength(0);
            ModifiedRow.Height = new GridLength(0);
            AccessedRow.Height = new GridLength(0);
            AttributesRow.Visibility = Visibility.Collapsed;
        }
    }

    private async Task ComputeSizeAsync()
    {
        _sizeCts = new CancellationTokenSource();
        var token = _sizeCts.Token;
        SizeText.Text = "Calculating...";

        long total;
        int fileCount = 0;
        int folderCount = 0;

        try
        {
            (total, fileCount, folderCount) = await Task.Run(() =>
            {
                long sum = 0;
                int files = 0;
                int folders = 0;

                foreach (var item in _selection)
                {
                    if (item.IsDirectory)
                    {
                        var (size, f, d) = SumDirectory(item.FullPath, token);
                        sum += size;
                        files += f;
                        folders += d;
                    }
                    else
                    {
                        sum += item.SizeBytes;
                        files++;
                    }
                }

                return (sum, files, folders);
            }, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        SizeText.Text = $"{FileSystemItem.FormatSize(total)} ({total:N0} bytes)";

        if (_selection.Any(i => i.IsDirectory))
        {
            ItemCountText.Text = $"{fileCount} file{(fileCount == 1 ? "" : "s")}, {folderCount} folder{(folderCount == 1 ? "" : "s")}";
            ItemCountLabel.Visibility = Visibility.Visible;
            ItemCountText.Visibility = Visibility.Visible;
        }
    }

    /// Recursively sums a folder's file sizes and counts its files/subfolders. Individual
    /// inaccessible entries are skipped rather than failing the whole walk.
    private static (long Size, int Files, int Folders) SumDirectory(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        long size = 0;
        int files = 0;
        int folders = 1;

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                token.ThrowIfCancellationRequested();

                if (Directory.Exists(entry))
                {
                    var (subSize, subFiles, subFolders) = SumDirectory(entry, token);
                    size += subSize;
                    files += subFiles;
                    folders += subFolders;
                }
                else
                {
                    try
                    {
                        size += new FileInfo(entry).Length;
                        files++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return (size, files, folders);
    }
}
