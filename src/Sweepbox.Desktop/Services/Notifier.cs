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
                WindowsNotifications.Show(title, message);
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
