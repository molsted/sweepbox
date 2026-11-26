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
    /// temporary batch file (<see cref="ElevatedScript"/>) that writes to a log, then replay the log.
    /// The timeout and cancellation also cover the time the UAC prompt is open; if it's answered late,
    /// the script is already gone and nothing runs.
    /// </summary>
    private async Task<CommandResult> RunElevatedWindowsAsync(CommandSpec spec, Action<string> onOutput, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        string[] userWritable =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            System.IO.Path.GetTempPath(),
        ];
        if (!ElevatedScript.IsSafeExecutable(spec.Executable, userWritable))
            return new CommandResult(-1, stopwatch.Elapsed,
                Error: $"Refusing to run {spec.Executable} as administrator from a folder your user account can change.");

        var tempDir = Directory.CreateTempSubdirectory("sweepbox-").FullName;
        try
        {
            var logFile = System.IO.Path.Combine(tempDir, "output.log");
            var script = System.IO.Path.Combine(tempDir, "run.cmd");
            try
            {
                await File.WriteAllTextAsync(script, ElevatedScript.Build(Path, spec.Environment, spec.Executable, spec.Arguments, logFile), cancellationToken);
            }
            catch (ArgumentException ex)
            {
                return new CommandResult(-1, stopwatch.Elapsed, Error: ex.Message);
            }

            var psi = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(script);

            // Process.Start blocks until the UAC prompt is answered; wait for it off-thread so the step's
            // timeout and Cancel still apply while the prompt is open.
            var start = Task.Run(() => Process.Start(psi));
            using (var promptWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                promptWait.CancelAfter(timeout);
                try
                {
                    await start.WaitAsync(promptWait.Token);
                }
                catch (OperationCanceledException)
                {
                    _ = start.ContinueWith(DisposeAbandoned, TaskScheduler.Default);
                    return new CommandResult(-1, stopwatch.Elapsed,
                        TimedOut: !cancellationToken.IsCancellationRequested, Cancelled: cancellationToken.IsCancellationRequested,
                        Error: cancellationToken.IsCancellationRequested ? null : "The administrator prompt wasn't answered in time.");
                }
            }

            using var process = await start;
            if (process is null)
                return new CommandResult(-1, stopwatch.Elapsed, Error: "Could not start elevated process.");
            var remaining = timeout - stopwatch.Elapsed;
            var result = await WaitAsync(process, stopwatch, remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1), cancellationToken);
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
            try { Directory.Delete(tempDir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>A prompt answered after we gave up: observe its outcome and release the handle.</summary>
    private static void DisposeAbandoned(Task<Process?> start)
    {
        if (start.IsCompletedSuccessfully)
            start.Result?.Dispose();
        else
            _ = start.Exception; // observed, so it isn't reported as unobserved
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
