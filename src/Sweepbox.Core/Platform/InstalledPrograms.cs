using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Sweepbox.Core.Platform;

/// <summary>One "Apps &amp; features" entry (an Uninstall registry key). The size is Windows' own estimate, in KiB.</summary>
public sealed record InstalledProgram(string DisplayName, long? EstimatedSizeKb, bool SystemComponent, string? ParentKeyName);

/// <summary>Lists the programs Windows shows under "Apps &amp; features". Implementations never throw.</summary>
public interface IInstalledProgramsSource
{
    IReadOnlyList<InstalledProgram> Read();
}

/// <summary>Reads the Uninstall registry keys: HKLM (64- and 32-bit views) and HKCU.</summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryInstalledPrograms : IInstalledProgramsSource
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<InstalledProgram> Read()
    {
        var programs = new List<InstalledProgram>();
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Default),
                 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(UninstallKey);
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var key = uninstall.OpenSubKey(name);
                        if (key?.GetValue("DisplayName") is not string display || display.Length == 0) continue;
                        // EstimatedSize is a DWORD in KiB; anything else (e.g. a string) is treated as unknown.
                        long? kb = key.GetValue("EstimatedSize") is int size ? size : null;
                        var system = key.GetValue("SystemComponent") is int sc && sc == 1;
                        programs.Add(new InstalledProgram(display, kb, system, key.GetValue("ParentKeyName") as string));
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"Skipping uninstall entry {name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Could not read {hive}/{view} uninstall entries: {ex.Message}");
            }
        }
        return programs;
    }
}
