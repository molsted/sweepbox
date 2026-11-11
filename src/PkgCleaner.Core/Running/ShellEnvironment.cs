using System.Diagnostics;

namespace PkgCleaner.Core.Running;

/// <summary>
/// Works out the PATH the user sees in their terminal. Apps started from Finder/launchd get a
/// minimal PATH without Homebrew or nvm, so on macOS/Linux we ask the user's login shell.
/// </summary>
public static class ShellEnvironment
{
    private const string Marker = "__PKGCLEANER_PATH__";

    private static readonly string[] UnixFallbackDirs =
    [
        "/opt/homebrew/bin", "/opt/homebrew/sbin", "/usr/local/bin", "/usr/local/sbin",
        "/usr/bin", "/bin", "/usr/sbin", "/sbin",
    ];

    public static async Task<string> ResolvePathAsync(string extraPath, CancellationToken cancellationToken = default)
    {
        var dirs = new List<string>();
        dirs.AddRange(Split(extraPath));

        if (OperatingSystem.IsWindows())
        {
            // Re-read machine + user PATH so tools installed after the app started are found.
            dirs.AddRange(Split(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)));
            dirs.AddRange(Split(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User)));
            dirs.AddRange(Split(Environment.GetEnvironmentVariable("PATH")));
        }
        else
        {
            dirs.AddRange(Split(await QueryLoginShellPathAsync(cancellationToken)));
            dirs.AddRange(Split(Environment.GetEnvironmentVariable("PATH")));
            dirs.AddRange(UnixFallbackDirs);
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return string.Join(Path.PathSeparator, dirs.Where(d => d.Length > 0).Distinct(comparer));
    }

    public static string? FindExecutable(string name, string path)
    {
        if (Path.IsPathRooted(name))
            return File.Exists(name) ? name : null;

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];

        foreach (var dir in Split(path))
        foreach (var ext in extensions)
        {
            var candidate = Path.Combine(dir, name + ext);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    internal static string? ParseMarkedPath(string output)
    {
        var start = output.IndexOf(Marker, StringComparison.Ordinal);
        if (start < 0)
            return null;
        start += Marker.Length;
        var end = output.IndexOf(Marker, start, StringComparison.Ordinal);
        return end < 0 ? null : output[start..end];
    }

    private static async Task<string?> QueryLoginShellPathAsync(CancellationToken cancellationToken)
    {
        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (string.IsNullOrEmpty(shell) || !File.Exists(shell))
            shell = File.Exists("/bin/zsh") ? "/bin/zsh" : "/bin/sh";

        var psi = new ProcessStartInfo(shell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        // Interactive + login so both .zprofile (brew shellenv) and .zshrc (nvm, volta, fnm) are read.
        psi.ArgumentList.Add("-ilc");
        psi.ArgumentList.Add($"printf '%s' \"{Marker}$PATH{Marker}\"");

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return null;
            process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return ParseMarkedPath(await stdout);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IEnumerable<string> Split(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? []
            : path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
