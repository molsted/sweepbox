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
                _ = MacNotifications.Show(title, message);
            else if (OperatingSystem.IsWindows())
                WindowsNotifications.Show(title, message);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Notification failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Waits until posted notifications have been handed to the system, or until <paramref name="timeout"/>,
    /// so a CLI run does not exit before its notification is delivered. Completes at once off macOS.
    /// </summary>
    public static Task FlushAsync(TimeSpan timeout) =>
        OperatingSystem.IsMacOS() ? MacNotifications.WhenIdleAsync(timeout) : Task.CompletedTask;

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
