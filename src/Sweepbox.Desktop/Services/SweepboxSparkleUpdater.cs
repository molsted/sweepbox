using System.Diagnostics;
using NetSparkleUpdater;
using NetSparkleUpdater.Interfaces;
using NetSparkleUpdater.SignatureVerifiers;

namespace Sweepbox.Desktop.Services;

/// <summary>
/// SparkleUpdater with a real auto-install on macOS: the release zip holds the new .app bundle;
/// it replaces the running one in place and relaunches. Windows keeps the installer flow.
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
            var extract = Path.Combine(Path.GetTempPath(), "sweepbox-update");
            try
            {
                Directory.CreateDirectory(extract);
                foreach (var entry in Directory.EnumerateFileSystemEntries(extract))
                {
                    Directory.Delete(entry, recursive: true);
                }
                if (!Run("ditto", $"-x -k \"{downloadFilePath}\" \"{extract}\""))
                {
                    Trace.WriteLine("Update: could not extract the downloaded zip");
                    return;
                }

                var bundle = Directory.EnumerateFileSystemEntries(extract, "*.app").FirstOrDefault();
                var running = FindEnclosingAppBundle(Environment.ProcessPath);
                if (bundle is null || running is null)
                {
                    Trace.WriteLine("Update: zip has no .app bundle or the running app path is unknown");
                    return;
                }

                // Replacing the bundle of a running app is safe: the binary keeps executing
                // from its deleted inode until we quit, right after relaunching the new copy.
                Directory.Delete(running, recursive: true);
                if (!Run("cp", $"-R \"{bundle}\" \"{running}\""))
                {
                    Trace.WriteLine("Update: could not copy the new app bundle into place");
                    return;
                }
                Run("open", $"-a \"{running}\"");
                _quit();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Update install failed: {ex.Message}");
            }
            finally
            {
                try { Directory.Delete(extract, recursive: true); } catch (IOException) { }
                try { File.Delete(downloadFilePath); } catch (IOException) { }
            }
        });
    }

    /// <summary>.../Sweepbox.app/Contents/MacOS/Sweepbox -> .../Sweepbox.app</summary>
    private static string? FindEnclosingAppBundle(string? executablePath)
    {
        var dir = executablePath is null ? null : Path.GetDirectoryName(executablePath);
        var contents = dir is null ? null : Path.GetDirectoryName(dir);
        var bundle = contents is null ? null : Path.GetDirectoryName(contents);
        return bundle is not null && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? bundle : null;
    }

    private static bool Run(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is null)
            return false;
        process.WaitForExit();
        return process.ExitCode == 0;
    }
}
