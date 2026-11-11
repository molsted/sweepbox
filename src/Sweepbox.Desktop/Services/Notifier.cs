using System.Diagnostics;

namespace Sweepbox.Desktop.Services;

/// <summary>Native system notifications without extra dependencies.</summary>
public static class Notifier
{
    public static void Show(string title, string message)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                ShowMac(title, message);
            else if (OperatingSystem.IsWindows())
                ShowWindows(title, message);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Notification failed: {ex.Message}");
        }
    }

    private static void ShowMac(string title, string message)
    {
        var psi = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add($"display notification {AppleScriptString(message)} with title \"Sweepbox\" subtitle {AppleScriptString(title)}");
        Process.Start(psi)?.Dispose();
    }

    private static string AppleScriptString(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static void ShowWindows(string title, string message)
    {
        // Toast via the WinRT API from PowerShell, using PowerShell's registered AppUserModelID.
        static string Xml(string s) => System.Security.SecurityElement.Escape(s) ?? "";
        var script = $$"""
            [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null
            [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] > $null
            $xml = New-Object Windows.Data.Xml.Dom.XmlDocument
            $xml.LoadXml('<toast><visual><binding template="ToastGeneric"><text>Sweepbox: {{Xml(title).Replace("'", "''")}}</text><text>{{Xml(message).Replace("'", "''")}}</text></binding></visual></toast>')
            $appId = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe'
            [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId).Show([Windows.UI.Notifications.ToastNotification]::new($xml))
            """;
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", script })
            psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
    }

    public static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Could not open {path}: {ex.Message}");
        }
    }
}
