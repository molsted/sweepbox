using System.Diagnostics;

namespace Sweepbox.Desktop.Services;

/// <summary>
/// Windows toasts under Sweepbox's own AppUserModelID, so they show the Sweepbox name and icon.
/// Failures are only traced; nothing here throws to callers.
/// </summary>
internal static class WindowsNotifications
{
    internal const string AppUserModelId = "dk.sweepbox.desktop";

#if WINDOWS
    private const string RegistryKeyPath = @"Software\Classes\AppUserModelId\" + AppUserModelId;

    /// <summary>Registers the AUMID's display name and icon for the current user (idempotent).</summary>
    public static void Register()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
            key.SetValue("DisplayName", "Sweepbox", Microsoft.Win32.RegistryValueKind.String);
            key.SetValue("IconUri", Path.Combine(AppContext.BaseDirectory, "app-icon.png"), Microsoft.Win32.RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Could not register the notification identity: {ex.Message}");
        }
    }

    public static void Show(string title, string message)
    {
        try
        {
            static string Xml(string s) => System.Security.SecurityElement.Escape(s) ?? "";
            var doc = new Windows.Data.Xml.Dom.XmlDocument();
            doc.LoadXml($"<toast><visual><binding template=\"ToastGeneric\"><text>{Xml(title)}</text><text>{Xml(message)}</text></binding></visual></toast>");
            Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(AppUserModelId)
                .Show(new Windows.UI.Notifications.ToastNotification(doc));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Windows notification failed: {ex.Message}");
        }
    }
#else
    public static void Register() { }

    public static void Show(string title, string message) { }
#endif
}
