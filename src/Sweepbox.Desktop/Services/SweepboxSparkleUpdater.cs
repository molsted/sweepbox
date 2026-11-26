using System.Diagnostics;
using NetSparkleUpdater;
using NetSparkleUpdater.Interfaces;
using NetSparkleUpdater.SignatureVerifiers;
using Sweepbox.Core.Platform;

namespace Sweepbox.Desktop.Services;

/// <summary>
/// SparkleUpdater with a real auto-install on macOS: the release zip holds the new .app bundle;
/// it is staged next to the running one, swapped in with renames (rolled back on failure), and a helper
/// opens the new copy once we have exited.
/// Windows keeps the installer flow.
/// </summary>
public sealed class SweepboxSparkleUpdater : SparkleUpdater
{
    private readonly Action _quit;

    public SweepboxSparkleUpdater(string appcastUrl, ISignatureVerifier signatureVerifier, Action quit)
        : base(appcastUrl, signatureVerifier)
    {
        _quit = quit;
    }

    protected override async Task RunDownloadedInstaller(string downloadFilePath)
    {
        if (!OperatingSystem.IsMacOS())
        {
            await base.RunDownloadedInstaller(downloadFilePath);
            return;
        }

        await Task.Run(() =>
        {
            // A fresh, private (0700) folder: nothing else can swap the extracted bundle under us.
            var extract = Directory.CreateTempSubdirectory("sweepbox-update-").FullName;
            string? staged = null;
            try
            {
                if (!Run("/usr/bin/ditto", "-x", "-k", downloadFilePath, extract))
                {
                    Trace.WriteLine("Update: could not extract the downloaded zip");
                    return;
                }

                var bundle = Directory.EnumerateDirectories(extract, "*.app").FirstOrDefault();
                var running = FindEnclosingAppBundle(Environment.ProcessPath);
                if (bundle is null || running is null)
                {
                    Trace.WriteLine("Update: zip has no .app bundle or the running app path is unknown");
                    return;
                }

                // Copy next to the installed app first (same volume, so the swap is a rename), keeping
                // the code signature and extended attributes; the installed app is untouched until then.
                staged = AppBundleSwap.StagingPathFor(running);
                if (!Run("/usr/bin/ditto", bundle, staged))
                {
                    Trace.WriteLine("Update: could not copy the new app bundle next to the installed one");
                    return;
                }

                // Rename the old bundle aside, the new one in, and put the old one back if that fails.
                // The running binary keeps executing from its open files until we quit.
                AppBundleSwap.Replace(running, staged);
                staged = null;
                StartRelaunchAfterExit(running);
                _quit();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Update install failed: {ex.Message}");
            }
            finally
            {
                if (staged is not null)
                    try { Directory.Delete(staged, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                try { Directory.Delete(extract, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                try { File.Delete(downloadFilePath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        });
    }

    /// <summary>
    /// Opens the new bundle once this process has exited. Opening it while we still run would only
    /// bring the old instance to the front (LaunchServices reuses a running app with the same id),
    /// and a second copy would stop at the single-instance lock. The shell outlives us; it gives up
    /// waiting after 60 s and opens the app anyway.
    /// </summary>
    private static void StartRelaunchAfterExit(string appBundle)
    {
        const string script = """
            i=0
            while kill -0 "$1" 2>/dev/null && [ "$i" -lt 300 ]; do sleep 0.2; i=$((i+1)); done
            exec /usr/bin/open -n "$2"
            """;
        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-c", script, "sweepbox-relaunch", Environment.ProcessId.ToString(), appBundle })
            psi.ArgumentList.Add(arg);
        Process.Start(psi)?.Dispose();
    }

    /// <summary>.../Sweepbox.app/Contents/MacOS/Sweepbox -> .../Sweepbox.app</summary>
    private static string? FindEnclosingAppBundle(string? executablePath)
    {
        var dir = executablePath is null ? null : Path.GetDirectoryName(executablePath);
        var contents = dir is null ? null : Path.GetDirectoryName(dir);
        var bundle = contents is null ? null : Path.GetDirectoryName(contents);
        return bundle is not null && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? bundle : null;
    }

    private static bool Run(string fileName, params string[] arguments)
    {
        var psi = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi);
        if (process is null)
            return false;
        process.WaitForExit();
        return process.ExitCode == 0;
    }
}
