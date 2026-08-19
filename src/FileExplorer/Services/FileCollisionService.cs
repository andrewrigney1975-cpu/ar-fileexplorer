using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileExplorer.Services;

public enum CollisionAction { Overwrite, Skip, Rename, Cancel }

public sealed record CollisionResult(CollisionAction Action, string? RenameTo, bool ApplyToAll);

/// Asks the user how to handle a filename collision - Overwrite / Skip / Rename / Cancel - with an
/// optional "apply to all remaining conflicts" choice. Called from the background file-operation
/// queue thread; the dialog itself is shown on the UI thread via the dispatcher.
public static class FileCollisionService
{
    public static Task<CollisionResult> ResolveAsync(
        string conflictingName,
        string suggestedRename,
        bool allowApplyToAll,
        DispatcherQueue dispatcher,
        Func<XamlRoot> getXamlRoot)
    {
        var tcs = new TaskCompletionSource<CollisionResult>();

        dispatcher.TryEnqueue(async () =>
        {
            // XamlRoot must be read on the UI thread - the caller runs on the background file-op
            // queue thread, so it hands in a getter rather than a pre-fetched value.
            var xamlRoot = getXamlRoot();

            var applyToAllBox = new CheckBox
            {
                Content = "Apply to all remaining conflicts",
                IsEnabled = allowApplyToAll,
                // CheckBox.IsChecked defaults to null (indeterminate) when unset, which renders as a
                // filled/checked-looking box - must be explicit or this silently starts "checked".
                IsChecked = false,
            };

            CollisionAction chosen = CollisionAction.Cancel;
            ContentDialog? dialog = null;

            // All four choices are equally weighted actions, not "3 real buttons + an odd extra one" -
            // ContentDialog only offers 3 built-in button roles, so every choice is drawn the same way
            // as plain content buttons instead of splitting them across the built-in footer.
            Button MakeChoice(string text, CollisionAction action)
            {
                var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch };
                button.Click += (_, _) =>
                {
                    chosen = action;
                    dialog!.Hide();
                };
                return button;
            }

            var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttonRow.Children.Add(MakeChoice("Overwrite", CollisionAction.Overwrite));
            buttonRow.Children.Add(MakeChoice("Skip", CollisionAction.Skip));
            buttonRow.Children.Add(MakeChoice($"Rename to \"{Path.GetFileName(suggestedRename)}\"", CollisionAction.Rename));
            buttonRow.Children.Add(MakeChoice("Cancel", CollisionAction.Cancel));

            dialog = new ContentDialog
            {
                Title = "Item already exists",
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"\"{conflictingName}\" already exists in the destination. What should happen to it?",
                            TextWrapping = TextWrapping.Wrap,
                        },
                        applyToAllBox,
                        buttonRow,
                    },
                },
                // No built-in Primary/Secondary/Close buttons - the four choices above are the only
                // way to answer. Dismissing (ESC / clicking outside) still needs a definite outcome,
                // so it's treated the same as an explicit Cancel via the `chosen` default above.
                XamlRoot = xamlRoot,
            };

            await dialog.ShowAsync();

            // Rename is included here too: for each subsequent collision the caller re-suggests a
            // fresh unique name (it never reuses this one item's exact suggested name), so "apply to
            // all" + Rename correctly reads as "silently auto-rename every remaining collision."
            var applyToAll = allowApplyToAll && applyToAllBox.IsChecked == true && chosen != CollisionAction.Cancel;

            tcs.SetResult(new CollisionResult(chosen, suggestedRename, applyToAll));
        });

        return tcs.Task;
    }
}
