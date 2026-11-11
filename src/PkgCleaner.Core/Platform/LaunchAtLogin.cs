using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace PkgCleaner.Core.Platform;

/// <summary>Registers the app to start when the user logs in.</summary>
public static class LaunchAtLogin
{
    private const string Label = "dk.pkgcleaner.agent";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "PkgCleaner";

    public static string MacPlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", Label + ".plist");

    public static void Apply(bool enabled, string? executablePath = null)
    {
        executablePath ??= Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");
        if (OperatingSystem.IsMacOS())
            ApplyMac(enabled, executablePath);
        else if (OperatingSystem.IsWindows())
            ApplyWindows(enabled, executablePath);
    }

    private static void ApplyMac(bool enabled, string executablePath)
    {
        if (!enabled)
        {
            File.Delete(MacPlistPath);
            return;
        }

        // Prefer launching the .app bundle via `open` so macOS treats it as a normal app launch.
        var bundle = FindEnclosingAppBundle(executablePath);
        var args = bundle is null
            ? $"<string>{SecurityElement.Escape(executablePath)}</string>"
            : $"<string>/usr/bin/open</string><string>-a</string><string>{SecurityElement.Escape(bundle)}</string>";

        Directory.CreateDirectory(Path.GetDirectoryName(MacPlistPath)!);
        File.WriteAllText(MacPlistPath, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{Label}</string>
              <key>ProgramArguments</key><array>{args}</array>
              <key>RunAtLoad</key><true/>
              <key>ProcessType</key><string>Interactive</string>
            </dict>
            </plist>
            """);
    }

    internal static string? FindEnclosingAppBundle(string executablePath)
    {
        // .../PkgCleaner.app/Contents/MacOS/PkgCleaner
        var dir = Path.GetDirectoryName(executablePath);
        var contents = dir is null ? null : Path.GetDirectoryName(dir);
        var bundle = contents is null ? null : Path.GetDirectoryName(contents);
        return bundle is not null && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? bundle : null;
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindows(bool enabled, string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(RunValue, $"\"{executablePath}\"");
        else
            key.DeleteValue(RunValue, throwOnMissingValue: false);
    }
}
