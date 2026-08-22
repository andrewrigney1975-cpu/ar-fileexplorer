using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace FileExplorer.Services;

/// Thin wrapper around Windows toast notifications. This app is unpackaged (no Package.appxmanifest),
/// which makes AppNotificationManager registration/activation the least battle-tested corner of the
/// app - every call here is defensive so a platform quirk never crashes or blocks the app.
public static class NotificationService
{
    private static bool _registered;

    public static void Register()
    {
        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            // Every future Show() call silently no-ops for the rest of the app session once this
            // fails - worth a trail since there's otherwise zero indication toasts are dead.
            LoggingService.LogWarning("NotificationService.Register", ex);
            _registered = false;
        }
    }

    public static void Show(string title, string message)
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(message)
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            // Best-effort: a failed toast should never take down the app.
            LoggingService.LogWarning($"NotificationService.Show: {title}", ex);
        }
    }
}
