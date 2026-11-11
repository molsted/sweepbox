using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Sweepbox.Core.Running;

public sealed class ProcessCommandRunner(string path) : ICommandRunner
{
    public string Path { get; } = path;

    public string? ResolveExecutable(string name) => ShellEnvironment.FindExecutable(name, Path);

    public async Task<CommandResult> RunAsync(CommandSpec spec, Action<string> onOutput, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (spec.Elevate && OperatingSystem.IsWindows() && !Environment.IsPrivilegedProcess)
            return await RunElevatedWindowsAsync(spec, onOutput, timeout, cancellationToken);

        var psi = new ProcessStartInfo(spec.Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in spec.Arguments)
            psi.ArgumentList.Add(arg);
        psi.Environment["PATH"] = Path;
        foreach (var (key, value) in spec.Environment)
            psi.Environment[key] = value;

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        // stdout and stderr arrive on different threads; serialize so callers needn't be thread-safe.
        var outputLock = new Lock();
        void Emit(string? line)
        {
            if (line is null)
                return;
            lock (outputLock)
                onOutput(line);
        }
        process.OutputDataReceived += (_, e) => Emit(e.Data);
        process.ErrorDataReceived += (_, e) => Emit(e.Data);

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new CommandResult(-1, stopwatch.Elapsed, Error: ex.Message);
        }

        // No stdin: anything that wants a password (sudo, prompts) fails fast instead of hanging.
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return await WaitAsync(process, stopwatch, timeout, cancellationToken);
    }

    private static async Task<CommandResult> WaitAsync(Process process, Stopwatch stopwatch, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
            // Flush any remaining async output events.
            process.WaitForExit();
            return new CommandResult(process.ExitCode, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new CommandResult(-1, stopwatch.Elapsed,
                TimedOut: timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested,
                Cancelled: cancellationToken.IsCancellationRequested);
        }
    }

    /// <summary>
    /// UAC-elevated processes can't have their output redirected, so run the command through a
    /// temporary batch file that writes to a log, then replay the log.
    /// </summary>
    private async Task<CommandResult> RunElevatedWindowsAsync(CommandSpec spec, Action<string> onOutput, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var tempDir = Directory.CreateTempSubdirectory("sweepbox-").FullName;
        var logFile = System.IO.Path.Combine(tempDir, "output.log");
        var script = System.IO.Path.Combine(tempDir, "run.cmd");
        var args = string.Join(' ', spec.Arguments.Select(a => $"\"{a.Replace("\"", "\"\"")}\""));
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine($"set \"PATH={Path}\"");
        foreach (var (key, value) in spec.Environment)
            sb.AppendLine($"set \"{key}={value}\"");
        sb.AppendLine($"\"{spec.Executable}\" {args} > \"{logFile}\" 2>&1");
        sb.AppendLine("exit /b %ERRORLEVEL%");
        await File.WriteAllTextAsync(script, sb.ToString(), cancellationToken);

        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(script);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return new CommandResult(-1, stopwatch.Elapsed, Error: "Could not start elevated process.");
            var result = await WaitAsync(process, stopwatch, timeout, cancellationToken);
            if (File.Exists(logFile))
                foreach (var line in await File.ReadAllLinesAsync(logFile, CancellationToken.None))
                    onOutput(line);
            return result;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new CommandResult(-1, stopwatch.Elapsed, Error: "Administrator permission was declined.");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }
}
