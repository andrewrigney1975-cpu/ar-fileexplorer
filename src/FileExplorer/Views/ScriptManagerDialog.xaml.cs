using FileExplorer.Services;
using FileExplorer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileExplorer.Views;

public sealed partial class ScriptManagerDialog : UserControl
{
    private const string NewScriptTemplate =
        "// selection() returns the active pane's selected files/folders\n" +
        "// each item has: Name, FullPath, IsDirectory, Size, Extension\n" +
        "for (const item of selection()) {\n" +
        "    log(item.Name);\n" +
        "}\n";

    public MainViewModel? MainViewModel { get; set; }

    public PaneViewModel? ActivePane { get; set; }

    public Action? RequestClose { get; set; }

    public ScriptManagerDialog()
    {
        InitializeComponent();
        ReferenceText.Text = ApiReferenceText;
        Loaded += (_, _) => RefreshList();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => RequestClose?.Invoke();

    public void HideCloseButton() => CloseButtonElement.Visibility = Visibility.Collapsed;

    private void RefreshList()
    {
        var selectedName = NameBox.Text;
        var names = ScriptService.List();
        ScriptsList.ItemsSource = names;

        if (!string.IsNullOrEmpty(selectedName) && names.Contains(selectedName))
        {
            ScriptsList.SelectedItem = selectedName;
        }
    }

    private void ScriptsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScriptsList.SelectedItem is not string name)
        {
            return;
        }

        NameBox.Text = name;
        CodeBox.Text = ScriptService.Load(name) ?? string.Empty;
        OutputBox.Text = string.Empty;
    }

    private void NewScript_Click(object sender, RoutedEventArgs e)
    {
        ScriptsList.SelectedItem = null;
        NameBox.Text = "New Script";
        CodeBox.Text = NewScriptTemplate;
        OutputBox.Text = string.Empty;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        ScriptService.Save(name, CodeBox.Text);
        RefreshList();
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (MainViewModel is null)
        {
            return;
        }

        RunningRing.IsActive = true;
        var result = await ScriptEngineService.RunAsync(CodeBox.Text, ActivePane, MainViewModel, DispatcherQueue, XamlRoot);
        RunningRing.IsActive = false;

        var lines = new List<string>(result.Log);
        if (!result.Success)
        {
            lines.Add($"Error: {result.Error}");
        }

        OutputBox.Text = lines.Count == 0 ? "(no output)" : string.Join("\n", lines);
    }

    private const string ApiReferenceText =
        "VALUES & FUNCTIONS\n" +
        "-------------------\n" +
        "currentPath                        Active pane's current folder (string)\n" +
        "selection()                        Selected files/folders in the active pane\n" +
        "addedFiles                         Files that triggered this run (folder-watch triggers only; else empty)\n" +
        "listFiles(path)                    Non-recursive folder listing\n" +
        "                                    Each item: { Name, FullPath, IsDirectory, Size, Extension }\n" +
        "exists(path)                       true if a file or folder exists at path\n" +
        "readText(path)                     Read a text file's contents\n" +
        "writeText(path, content)           Write (overwrite) a text file\n" +
        "createFolder(path)                 Create a folder (and any missing parents)\n" +
        "rename(path, newName)              Rename a file or folder in place\n" +
        "copyTo(path, destFolder)           Copy a file or folder into destFolder\n" +
        "moveTo(path, destFolder)           Move a file or folder into destFolder\n" +
        "deleteItem(path, permanent)        Delete; permanent=false (default use) sends to Recycle Bin\n" +
        "prompt(message, defaultValue)      Ask the user for text; returns null if cancelled\n" +
        "confirm(message)                   Ask a yes/no question; returns true/false\n" +
        "notify(title, message)             Show a Windows toast notification\n" +
        "refresh()                          Refresh every open pane after making changes\n" +
        "log(message)                       Add a line to this run's output\n" +
        "\n" +
        "PRINCIPLES\n" +
        "----------\n" +
        "- Plain JavaScript (ES5.1, via the Jint interpreter).\n" +
        "- Scripts can also run unattended: bound to a watched folder (Manage Automation...) or on an interval schedule.\n" +
        "- File-item properties use .NET casing: item.Name, item.FullPath, not item.name.\n" +
        "- A script has 30 seconds to finish before it's stopped automatically.\n" +
        "- File changes made by scripts are NOT tracked by Undo - double-check destructive scripts before running them.\n" +
        "- deleteItem sends to the Recycle Bin by default; pass true only when you really mean permanent.\n" +
        "- Scripts have the same file-system access as the app itself - there's no security sandbox beyond the timeout.";

    private async void DeleteScript_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Delete Script",
            Content = $"Delete the script \"{name}\"? This can't be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        ScriptService.Delete(name);

        if (NameBox.Text == name)
        {
            NewScript_Click(sender, e);
        }

        RefreshList();
    }
}
