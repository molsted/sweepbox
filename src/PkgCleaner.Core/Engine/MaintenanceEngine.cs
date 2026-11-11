using PkgCleaner.Core.Managers;
using PkgCleaner.Core.Running;
using PkgCleaner.Core.Settings;

namespace PkgCleaner.Core.Engine;

public sealed record EngineStatus(bool IsRunning, string? CurrentManager, string? CurrentStep, RunReport? LastRun);

/// <summary>Runs the enabled package managers one at a time and records what happened.</summary>
public sealed class MaintenanceEngine
{
    private readonly Func<AppSettings> _settings;
    private readonly Func<AppSettings, Task<ICommandRunner>> _runnerFactory;
    private readonly IReadOnlyList<PackageManager> _managers;
    private readonly RunHistoryStore _history;
    private readonly string _logDirectory;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;

    public MaintenanceEngine(
        Func<AppSettings> settings,
        Func<AppSettings, Task<ICommandRunner>> runnerFactory,
        IReadOnlyList<PackageManager> managers,
        RunHistoryStore history,
        string logDirectory,
        TimeProvider? time = null)
    {
        _settings = settings;
        _runnerFactory = runnerFactory;
        _managers = managers;
        _history = history;
        _logDirectory = logDirectory;
        _time = time ?? TimeProvider.System;
        Status = new EngineStatus(false, null, null, history.Latest);
    }

    public EngineStatus Status { get; private set; }

    public IReadOnlyList<PackageManager> Managers => _managers;

    /// <summary>Raised from a background thread.</summary>
    public event Action<EngineStatus>? StatusChanged;

    /// <summary>Every log line as it is written. Raised from a background thread.</summary>
    public event Action<string>? Output;

    public event Action<RunReport>? RunCompleted;

    public void Cancel() => _cts?.Cancel();

    /// <summary>Runs maintenance. Returns null if a run is already in progress.</summary>
    /// <param name="onlyManagerIds">Restrict to these managers (they are run even if disabled in settings).</param>
    public async Task<RunReport?> RunAsync(RunTrigger trigger, IReadOnlyCollection<string>? onlyManagerIds = null)
    {
        if (!await _gate.WaitAsync(0))
            return null;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var settings = _settings();
        var report = new RunReport { StartedAt = _time.GetLocalNow(), Trigger = trigger };
        StreamWriter? log = null;

        try
        {
            Directory.CreateDirectory(_logDirectory);
            report.LogFile = Path.Combine(_logDirectory, $"run-{report.StartedAt:yyyyMMdd-HHmmss}.log");
            log = new StreamWriter(report.LogFile, append: true) { AutoFlush = true };
            PruneLogs();

            void Write(string line)
            {
                var stamped = $"{_time.GetLocalNow():HH:mm:ss}  {line}";
                lock (log) log.WriteLine(stamped);
                Output?.Invoke(stamped);
            }

            SetStatus(true, null, "Preparing…");
            Write($"=== {trigger} run started {report.StartedAt:yyyy-MM-dd HH:mm:ss} ===");
            var runner = await _runnerFactory(settings);
            var timeout = TimeSpan.FromMinutes(settings.StepTimeoutMinutes);

            var selected = _managers.Where(m => m.IsSupportedOnThisOS &&
                (onlyManagerIds is null ? m.IsEnabled(settings) : onlyManagerIds.Contains(m.Id)));

            foreach (var manager in selected)
            {
                var managerReport = new ManagerReport { Id = manager.Id, DisplayName = manager.DisplayName };
                report.Managers.Add(managerReport);

                if (token.IsCancellationRequested)
                {
                    managerReport.Outcome = Outcome.Cancelled;
                    continue;
                }

                Write("");
                Write($"--- {manager.DisplayName} ---");
                var executable = runner.ResolveExecutable(manager.ExecutableName);
                if (executable is null)
                {
                    managerReport.Outcome = Outcome.Skipped;
                    managerReport.Message = $"'{manager.ExecutableName}' not found on PATH";
                    Write($"Skipped: {managerReport.Message}");
                    continue;
                }

                var steps = manager.BuildSteps(settings, executable);
                if (steps.Count == 0)
                {
                    managerReport.Outcome = Outcome.Skipped;
                    managerReport.Message = "No steps enabled";
                    Write("Skipped: no steps enabled");
                    continue;
                }

                foreach (var step in steps)
                {
                    if (token.IsCancellationRequested)
                        break;

                    SetStatus(true, manager.DisplayName, step.Description);
                    Write($"> {step.Description}: {step.CommandLine}");
                    var result = await runner.RunAsync(step, line => Write("  " + line), timeout, token);

                    var stepReport = new StepReport
                    {
                        Description = step.Description,
                        CommandLine = step.CommandLine,
                        ExitCode = result.ExitCode,
                        DurationSeconds = Math.Round(result.Duration.TotalSeconds, 1),
                        Outcome = result.Cancelled ? Outcome.Cancelled : result.Success ? Outcome.Succeeded : Outcome.Failed,
                        Message = result.Error ?? (result.TimedOut ? $"Timed out after {settings.StepTimeoutMinutes} min" : null),
                    };
                    managerReport.Steps.Add(stepReport);
                    Write($"{(result.Success ? "OK" : "FAILED")} ({stepReport.DurationSeconds}s, exit {result.ExitCode}){(stepReport.Message is null ? "" : " - " + stepReport.Message)}");
                }

                managerReport.Outcome =
                    token.IsCancellationRequested ? Outcome.Cancelled
                    : managerReport.Steps.Any(s => s.Outcome == Outcome.Failed) ? Outcome.Failed
                    : Outcome.Succeeded;
                if (managerReport.Outcome == Outcome.Failed)
                    managerReport.Message = string.Join("; ", managerReport.Steps
                        .Where(s => s.Outcome == Outcome.Failed)
                        .Select(s => s.Message is null ? s.Description : $"{s.Description}: {s.Message}"));
            }

            report.FinishedAt = _time.GetLocalNow();
            Write("");
            Write($"=== Finished: {report.Outcome} in {(report.FinishedAt - report.StartedAt).TotalMinutes:0.0} min ===");
        }
        catch (Exception ex)
        {
            report.FinishedAt = _time.GetLocalNow();
            report.Managers.Add(new ManagerReport { Id = "app", DisplayName = "PkgCleaner", Outcome = Outcome.Failed, Message = ex.Message });
            try { log?.WriteLine($"Unexpected error: {ex}"); } catch (ObjectDisposedException) { }
        }
        finally
        {
            log?.Dispose();
            _history.Add(report);
            _cts.Dispose();
            _cts = null;
            Status = new EngineStatus(false, null, null, report);
            _gate.Release();
        }

        StatusChanged?.Invoke(Status);
        RunCompleted?.Invoke(report);
        return report;
    }

    private void SetStatus(bool running, string? manager, string? step)
    {
        Status = new EngineStatus(running, manager, step, Status.LastRun);
        StatusChanged?.Invoke(Status);
    }

    private void PruneLogs(int keep = 60)
    {
        foreach (var old in new DirectoryInfo(_logDirectory).GetFiles("run-*.log")
                     .OrderByDescending(f => f.Name).Skip(keep))
        {
            try { old.Delete(); } catch (IOException) { }
        }
    }
}
