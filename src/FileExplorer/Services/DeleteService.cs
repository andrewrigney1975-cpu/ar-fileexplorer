using FileExplorer.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileExplorer.Services;

/// Shared by every delete entry point (context menu, keyboard shortcut) so behavior - confirmation
/// dialog, Recycle Bin vs permanent, remote handling, failure reporting - stays identical regardless
/// of how the delete was triggered. Takes an explicit XamlRoot/refresh callback instead of assuming
/// a PaneView instance, so it can be driven purely from a PaneViewModel's tracked selection (see
/// MainWindow's global Delete/Shift+Delete keyboard accelerators, which work off the active pane's
/// ViewModel state rather than literal keyboard focus - a marquee-drag multi-select never focuses
/// the ListView itself, so a routed-KeyDown-based handler would silently never fire for it).
public static class DeleteService
{
    public static async Task DeleteItemsAsync(IReadOnlyList<FileSystemItem> items, bool permanent, XamlRoot xamlRoot, Action refresh)
    {
        if (items.Count == 0)
        {
            return;
        }

        var hasRemote = items.Any(i => i.IsRemote);

        if (permanent || hasRemote)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "Delete permanently?",
                Content = hasRemote
                    ? $"{items.Count} item{(items.Count == 1 ? "" : "s")} will be deleted permanently - there is no Recycle Bin for a remote connection. This can't be undone."
                    : $"{items.Count} item{(items.Count == 1 ? "" : "s")} will be deleted permanently. This can't be undone.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        var paths = items.Where(i => !i.IsRemote).Select(i => i.FullPath).ToList();
        var failures = new List<(string Path, string Error)>();

        await Task.Run(() =>
        {
            foreach (var path in paths)
            {
                try
                {
                    DeleteOne(path, permanent);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Skip items that fail (locked, permissions, non-empty edge cases) and continue
                    // with the rest, but the failure must be visible - silently doing nothing here
                    // makes a real error indistinguishable from "it just didn't work".
                    failures.Add((path, ex.Message));
                }
            }
        });

        // No Undo support for remote delete (remote operations never push an UndoAction) - always
        // permanent, since there's no remote Recycle Bin equivalent, which the dialog above warns
        // about explicitly for a remote-containing selection.
        foreach (var item in items.Where(i => i.IsRemote))
        {
            if (!RemotePathService.TryParse(item.FullPath, out _, out var connectionId, out var remotePath))
            {
                continue;
            }

            var session = RemoteSessionManager.TryGetSession(connectionId);
            if (session is null)
            {
                failures.Add((item.FullPath, "Not connected."));
                continue;
            }

            try
            {
                if (item.IsDirectory)
                {
                    await FileOperationQueueService.DeleteRemoteDirectoryRecursiveAsync(session, remotePath, CancellationToken.None);
                }
                else
                {
                    await session.DeleteFileAsync(remotePath, CancellationToken.None);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                failures.Add((item.FullPath, ex.Message));
            }
        }

        refresh();

        if (failures.Count > 0)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = failures.Count == 1 ? "Couldn't delete item" : $"Couldn't delete {failures.Count} items",
                Content = new TextBlock
                {
                    Text = string.Join("\n\n", failures.Select(f => FormatDeleteFailure(f.Path, f.Error))),
                    TextWrapping = TextWrapping.Wrap,
                },
                CloseButtonText = "Close",
            };

            await dialog.ShowAsync();
        }
    }

    /// "Access is denied" on a non-empty folder is almost always either a locked/open file inside
    /// it or (very commonly, when the path is under OneDrive/etc.) an online-only cloud placeholder
    /// that hasn't finished downloading - the raw exception text alone doesn't hint at either.
    private static string FormatDeleteFailure(string path, string error)
    {
        var isAccessDenied = error.Contains("denied", StringComparison.OrdinalIgnoreCase);
        var hint = isAccessDenied
            ? CloudProviderService.IsUnderCloudRoot(path)
                ? " This folder is inside a cloud-sync location - a file inside it may still be online-only and not fully downloaded yet. Try opening the folder and waiting for it to finish syncing, then delete again."
                : " A file inside this folder may be open in another program, or read-only/protected."
            : string.Empty;

        return $"{Path.GetFileName(path)}:\n{error}{hint}";
    }

    private static void DeleteOne(string path, bool permanent)
    {
        var isDirectory = Directory.Exists(path);

        if (permanent)
        {
            if (isDirectory)
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        if (isDirectory)
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        else if (File.Exists(path))
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
    }
}
