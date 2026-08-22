using FileExplorer.ViewModels;
using Jint;
using Jint.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileExplorer.Services;

public sealed record ScriptRunResult(bool Success, string? Error, IReadOnlyList<string> Log);

/// Plain interop shape handed to scripts for a file/folder - deliberately not the app's real
/// FileSystemItem, which carries ObservableObject/UI-thread-bound state that has no business
/// crossing the script boundary.
public sealed class ScriptFileItem
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public string Extension { get; init; } = string.Empty;
}

/// Runs a user script in a fresh Jint engine bound to a small, curated API surface. Scripts run
/// off the UI thread so blocking calls like prompt()/confirm() can dispatch a ContentDialog to the
/// UI thread and wait on it without deadlocking.
public static class ScriptEngineService
{
    private const int TimeoutSeconds = 30;
    private const int MaxStatements = 2_000_000;

    public static async Task<ScriptRunResult> RunAsync(
        string code,
        PaneViewModel? activePane,
        MainViewModel mainViewModel,
        DispatcherQueue dispatcher,
        XamlRoot xamlRoot,
        IReadOnlyList<string>? addedFilePaths = null)
    {
        var log = new List<string>();

        try
        {
            await Task.Run(() =>
            {
                var engine = new Engine(options =>
                {
                    options.TimeoutInterval(TimeSpan.FromSeconds(TimeoutSeconds));
                    options.MaxStatements(MaxStatements);
                });

                BindApi(engine, activePane, mainViewModel, dispatcher, xamlRoot, log, addedFilePaths);

                engine.Execute(code);
            });

            return new ScriptRunResult(true, null, log);
        }
        catch (JavaScriptException ex)
        {
            return new ScriptRunResult(false, ex.Message, log);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ScriptRunResult(false, ex.Message, log);
        }
    }

    private static void BindApi(
        Engine engine,
        PaneViewModel? activePane,
        MainViewModel mainViewModel,
        DispatcherQueue dispatcher,
        XamlRoot xamlRoot,
        List<string> log,
        IReadOnlyList<string>? addedFilePaths)
    {
        engine.SetValue("currentPath", activePane?.CurrentPath ?? string.Empty);

        engine.SetValue("addedFiles", (addedFilePaths ?? Array.Empty<string>())
            .Select(ToScriptItem)
            .ToList());

        engine.SetValue("selection", new Func<List<ScriptFileItem>>(() =>
        {
            if (activePane is null)
            {
                return new List<ScriptFileItem>();
            }

            var items = activePane.SelectedItems.Count > 0
                ? activePane.SelectedItems
                : activePane.SelectedItem is { } single ? new List<Models.FileSystemItem> { single } : new List<Models.FileSystemItem>();

            return items.Select(ToScriptItem).ToList();
        }));

        engine.SetValue("listFiles", new Func<string, List<ScriptFileItem>>(path =>
            App.Services.GetRequiredService<IFileSystemService>().GetItems(path).Select(ToScriptItem).ToList()));

        engine.SetValue("exists", new Func<string, bool>(path => File.Exists(path) || Directory.Exists(path)));

        engine.SetValue("readText", new Func<string, string>(File.ReadAllText));

        engine.SetValue("writeText", new Action<string, string>(File.WriteAllText));

        engine.SetValue("createFolder", new Action<string>(path => Directory.CreateDirectory(path)));

        engine.SetValue("rename", new Action<string, string>((path, newName) =>
        {
            var destination = Path.Combine(Path.GetDirectoryName(path)!, newName);
            if (Directory.Exists(path))
            {
                Directory.Move(path, destination);
            }
            else if (File.Exists(path))
            {
                File.Move(path, destination);
            }
        }));

        engine.SetValue("copyTo", new Action<string, string>((path, destFolder) =>
        {
            var destination = FileOperationService.MakeUniqueDestination(
                Path.Combine(destFolder, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))));

            if (Directory.Exists(path))
            {
                CopyDirectory(path, destination);
            }
            else if (File.Exists(path))
            {
                Directory.CreateDirectory(destFolder);
                File.Copy(path, destination);
            }
        }));

        engine.SetValue("moveTo", new Action<string, string>((path, destFolder) =>
        {
            var destination = FileOperationService.MakeUniqueDestination(
                Path.Combine(destFolder, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))));

            if (Directory.Exists(path))
            {
                Directory.Move(path, destination);
            }
            else if (File.Exists(path))
            {
                Directory.CreateDirectory(destFolder);
                File.Move(path, destination);
            }
        }));

        engine.SetValue("deleteItem", new Action<string, bool>((path, permanent) => DeleteOne(path, permanent)));

        engine.SetValue("log", new Action<object>(message => log.Add(message?.ToString() ?? "null")));

        engine.SetValue("notify", new Action<string, string>((title, message) =>
            dispatcher.TryEnqueue(() => NotificationService.Show(title, message))));

        engine.SetValue("refresh", new Action(() =>
            dispatcher.TryEnqueue(() => mainViewModel.RefreshAllPanes())));

        engine.SetValue("prompt", new Func<string, string, string?>((message, defaultValue) =>
        {
            var tcs = new TaskCompletionSource<string?>();

            dispatcher.TryEnqueue(async () =>
            {
                var input = new TextBox { Text = defaultValue ?? string.Empty };
                var dialog = new ContentDialog
                {
                    Title = "Script Input",
                    Content = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, input } },
                    PrimaryButtonText = "OK",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = xamlRoot,
                };

                var result = await dialog.ShowAsync();
                tcs.SetResult(result == ContentDialogResult.Primary ? input.Text : null);
            });

            return tcs.Task.GetAwaiter().GetResult();
        }));

        engine.SetValue("confirm", new Func<string, bool>(message =>
        {
            var tcs = new TaskCompletionSource<bool>();

            dispatcher.TryEnqueue(async () =>
            {
                var dialog = new ContentDialog
                {
                    Title = "Script Confirmation",
                    Content = message,
                    PrimaryButtonText = "Yes",
                    CloseButtonText = "No",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = xamlRoot,
                };

                tcs.SetResult(await dialog.ShowAsync() == ContentDialogResult.Primary);
            });

            return tcs.Task.GetAwaiter().GetResult();
        }));
    }

    private static ScriptFileItem ToScriptItem(string path)
    {
        var isDirectory = Directory.Exists(path);
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));

        if (isDirectory)
        {
            return new ScriptFileItem { Name = name, FullPath = path, IsDirectory = true };
        }

        var info = new FileInfo(path);
        return new ScriptFileItem
        {
            Name = name,
            FullPath = path,
            IsDirectory = false,
            Size = info.Exists ? info.Length : 0,
            Extension = info.Extension,
        };
    }

    private static ScriptFileItem ToScriptItem(Models.FileSystemItem item) => new()
    {
        Name = item.Name,
        FullPath = item.FullPath,
        IsDirectory = item.IsDirectory,
        Size = item.SizeBytes,
        Extension = item.Extension,
    };

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
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
