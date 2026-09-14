using FileExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileExplorer.Views;

/// Shared PIN dialogs for encrypting/unlocking a folder, used from both the pane's context menu
/// ("Encrypt Folder...", "Unlock Encrypted Folder...") and the empty-space menu inside an unlocked
/// encrypted folder ("Decrypt Folder..."). Kept separate from [[VirtualFolderDialogs]] and its PIN -
/// that PIN only gates a UI list; this one derives an actual AES-256 key, and the two must never be
/// confused or share a hash even if a future UI offers "same PIN for convenience."
internal static class EncryptedFolderDialogs
{
    private const int MinPinLength = 8;

    /// Prompts for a PIN (with confirmation) to encrypt a folder, enforcing the length/complexity
    /// floor, then runs the encryption with a progress dialog. Returns the new container's path, or
    /// null if the user cancelled or encryption failed (a failure leaves the original folder
    /// untouched - see EncryptedFolderService.EncryptFolderAsync's verify-before-wipe order).
    public static async Task<string?> EncryptFolderAsync(XamlRoot xamlRoot, string folderPath)
    {
        var pin = await PromptForNewPinAsync(xamlRoot,
            title: "Encrypt Folder",
            intro: $"This replaces \"{Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar))}\" with a single " +
                   "encrypted file. Anyone browsing this location (in this app, Windows Explorer, or a forensic disk " +
                   "tool) will see one opaque file, not the folder's contents. There is no recovery if the PIN is " +
                   "forgotten.");

        if (pin is null)
        {
            return null;
        }

        var confirmed = await new ContentDialog
        {
            Title = "Encrypt Folder",
            Content = "The original files will be overwritten and deleted once the encrypted copy is verified. Continue?",
            PrimaryButtonText = "Encrypt",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
        }.ShowAsync() == ContentDialogResult.Primary;

        if (!confirmed)
        {
            return null;
        }

        var progressText = new TextBlock { Text = "Starting...", TextWrapping = TextWrapping.Wrap };
        var progressBar = new ProgressBar { IsIndeterminate = true };
        var progressDialog = new ContentDialog
        {
            Title = "Encrypting...",
            Content = new StackPanel { Spacing = 8, Children = { progressBar, progressText } },
            XamlRoot = xamlRoot,
        };

        var progress = new Progress<string>(name => progressText.Text = name);
        var cts = new CancellationTokenSource();
        var showTask = progressDialog.ShowAsync();

        try
        {
            var containerPath = await EncryptedFolderService.EncryptFolderAsync(folderPath, pin, progress, cts.Token);
            progressDialog.Hide();
            return containerPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            progressDialog.Hide();
            LoggingService.LogWarning("EncryptedFolderDialogs.EncryptFolderAsync", ex);
            await new ContentDialog
            {
                Title = "Encryption failed",
                Content = ex.Message,
                CloseButtonText = "OK",
                XamlRoot = xamlRoot,
            }.ShowAsync();
            return null;
        }
    }

    /// Prompts for the PIN to unlock <paramref name="containerPath"/>. Returns true on success.
    public static async Task<bool> UnlockAsync(XamlRoot xamlRoot, string containerPath)
    {
        var pinBox = new PasswordBox { PlaceholderText = "PIN" };
        var dialog = new ContentDialog
        {
            Title = $"Unlock \"{Path.GetFileNameWithoutExtension(containerPath)}\"",
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

        if (await EncryptedFolderSession.TryUnlockAsync(containerPath, pinBox.Password, CancellationToken.None))
        {
            return true;
        }

        await new ContentDialog
        {
            Title = "Incorrect PIN",
            Content = "That PIN doesn't match, or this isn't a valid encrypted folder container.",
            CloseButtonText = "OK",
            XamlRoot = xamlRoot,
        }.ShowAsync();
        return false;
    }

    /// The permanent "remove encryption" escape hatch: decrypts every file back to a real folder
    /// beside the container, then deletes the container only after the decrypt succeeds.
    public static async Task<bool> DecryptPermanentlyAsync(XamlRoot xamlRoot, string containerPath)
    {
        var unlocked = EncryptedFolderSession.TryGetUnlocked(containerPath);
        if (unlocked is null)
        {
            return false;
        }

        var confirmed = await new ContentDialog
        {
            Title = "Decrypt Folder",
            Content = "This restores a real, unencrypted folder next to the container and deletes the container. " +
                      "The contents will then be visible to anyone with access to this location again.",
            PrimaryButtonText = "Decrypt",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
        }.ShowAsync() == ContentDialogResult.Primary;

        if (!confirmed)
        {
            return false;
        }

        var destination = UniqueFolderPath(Path.Combine(
            Path.GetDirectoryName(containerPath)!,
            Path.GetFileNameWithoutExtension(containerPath)));

        try
        {
            await EncryptedFolderService.DecryptAllAsync(containerPath, unlocked.Value.Key, unlocked.Value.Entries, destination, CancellationToken.None);
            EncryptedFolderSession.Lock(containerPath);
            File.Delete(containerPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.LogWarning("EncryptedFolderDialogs.DecryptPermanentlyAsync", ex);
            await new ContentDialog
            {
                Title = "Decryption failed",
                Content = ex.Message,
                CloseButtonText = "OK",
                XamlRoot = xamlRoot,
            }.ShowAsync();
            return false;
        }
    }

    private static async Task<string?> PromptForNewPinAsync(XamlRoot xamlRoot, string title, string intro)
    {
        var pinBox = new PasswordBox { PlaceholderText = $"PIN ({MinPinLength}+ characters, not just numbers)" };
        var confirmBox = new PasswordBox { PlaceholderText = "Confirm PIN" };

        var dialog = new ContentDialog
        {
            Title = title,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.8 },
                    pinBox,
                    confirmBox,
                },
            },
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        var pin = pinBox.Password;
        if (pin.Length < MinPinLength || pin != confirmBox.Password || pin.All(char.IsDigit))
        {
            await new ContentDialog
            {
                Title = "PIN not accepted",
                Content = $"PINs must be at least {MinPinLength} characters, include something other than digits, and match - try again.",
                CloseButtonText = "OK",
                XamlRoot = xamlRoot,
            }.ShowAsync();
            return null;
        }

        return pin;
    }

    private static string UniqueFolderPath(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return path;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{path} ({i})";
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
