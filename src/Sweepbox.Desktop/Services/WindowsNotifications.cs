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
            // XML 1.0 forbids most control characters and lone surrogates; LoadXml would throw on them.
            static string Xml(string s)
            {
                var clean = new System.Text.StringBuilder(s.Length);
                for (var i = 0; i < s.Length; i++)
                {
                    if (i + 1 < s.Length && System.Xml.XmlConvert.IsXmlSurrogatePair(s[i + 1], s[i]))
                    {
                        clean.Append(s[i]).Append(s[i + 1]);
                        i++;
                    }
                    else if (System.Xml.XmlConvert.IsXmlChar(s[i]))
                    {
                        clean.Append(s[i]);
                    }
                }
                return System.Security.SecurityElement.Escape(clean.ToString()) ?? "";
            }
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
