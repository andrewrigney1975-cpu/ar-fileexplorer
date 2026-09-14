using FileExplorer.Models;
using FileExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileExplorer.Views;

/// Shared "New Virtual Folder" / PIN dialogs used by both the left rail's + button
/// (MainWindow.xaml.cs) and the pane's "Add to Virtual Folder ▸ New Virtual Folder..." context menu
/// item (PaneView.xaml.cs), so the private-folder PIN flow only lives in one place.
internal static class VirtualFolderDialogs
{
    /// Prompts for a name (and whether it's private), setting up the PIN first if this is the first
    /// private virtual folder ever created. Returns null if the user cancelled at any step - a
    /// cancelled PIN setup aborts the whole creation rather than silently creating an unprotected
    /// "private" folder.
    public static async Task<VirtualFolder?> CreateAsync(XamlRoot xamlRoot)
    {
        var nameBox = new TextBox { PlaceholderText = "Virtual folder name" };
        var privateCheck = new CheckBox { Content = "Private (PIN-protected)" };

        var dialog = new ContentDialog
        {
            Title = "New Virtual Folder",
            Content = new StackPanel { Spacing = 8, Children = { nameBox, privateCheck } },
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(nameBox.Text))
        {
            return null;
        }

        var isPrivate = privateCheck.IsChecked == true;
        if (isPrivate && !VirtualFolderService.HasPin() && !await SetPinAsync(xamlRoot))
        {
            return null;
        }

        return VirtualFolderService.Create(nameBox.Text.Trim(), isPrivate);
    }

    /// First-time setup, triggered by creating the first private virtual folder.
    private static async Task<bool> SetPinAsync(XamlRoot xamlRoot)
    {
        var pinBox = new PasswordBox { PlaceholderText = "PIN (4+ characters)" };
        var confirmBox = new PasswordBox { PlaceholderText = "Confirm PIN" };

        var dialog = new ContentDialog
        {
            Title = "Set a PIN for Private Virtual Folders",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = "This PIN gates the \"Private Virtual Folders\" section in the left rail. " +
                               "It's stored on this PC only and can't be recovered if forgotten.",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        Opacity = 0.8,
                    },
                    pinBox,
                    confirmBox,
                },
            },
            PrimaryButtonText = "Set PIN",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return false;
        }

        if (pinBox.Password.Length < 4 || pinBox.Password != confirmBox.Password)
        {
            await new ContentDialog
            {
                Title = "PIN not set",
                Content = "PINs must be at least 4 characters and match - try again.",
                CloseButtonText = "OK",
                XamlRoot = xamlRoot,
            }.ShowAsync();
            return false;
        }

        VirtualFolderService.SetPin(pinBox.Password);
        return true;
    }

    /// Prompts for the PIN to expand the "Private Virtual Folders" rail section. The unlocked state
    /// this grants is session-only - callers are expected to hold it in an instance field that
    /// naturally resets on the next app launch, never persisting it themselves.
    public static async Task<bool> UnlockPrivateSectionAsync(XamlRoot xamlRoot)
    {
        var pinBox = new PasswordBox { PlaceholderText = "PIN" };
        var dialog = new ContentDialog
        {
            Title = "Private Virtual Folders",
            Content = pinBox,
            PrimaryButtonText = "Unlock",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return false;
        }

        if (VirtualFolderService.VerifyPin(pinBox.Password))
        {
            return true;
        }

        await new ContentDialog
        {
            Title = "Incorrect PIN",
            Content = "That PIN doesn't match.",
            CloseButtonText = "OK",
            XamlRoot = xamlRoot,
        }.ShowAsync();
        return false;
    }
}
