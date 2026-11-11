using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sweepbox.Core.Engine;
using Sweepbox.Desktop.Services;

namespace Sweepbox.Desktop.ViewModels;

public sealed record ManagerRow(string Name, string Outcome, bool IsOk, bool IsFailed, string Detail, string? Freed)
{
    public bool HasFreed => Freed is not null;
}

public sealed record HistoryRow(string When, string Trigger, string Outcome, bool IsFailed, string Duration, string Freed, string? LogFile);

public partial class StatusViewModel : ViewModelBase, IDisposable
{
    private const int MaxLogChars = 200_000;
    private readonly AppServices _services;
    private readonly Action _showSettings;
    private readonly StringBuilder _log = new();
    private readonly DispatcherTimer _timer;
    private bool _logDirty;

    public StatusViewModel(AppServices services, Action showSettings)
    {
        _services = services;
        _showSettings = showSettings;

        // Show the most recent log so the window isn't empty when idle.
        var lastLog = services.History.Latest?.LogFile;
        if (lastLog is not null && File.Exists(lastLog))
            _log.Append(File.ReadAllText(lastLog));
        _logText = _log.ToString();

        services.Engine.Output += OnOutput;
        services.Engine.StatusChanged += OnStatusChanged;
        services.Scheduler.Changed += OnSchedulerChanged;

        // Batch log updates; streaming every line to the UI would be needlessly expensive.
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
        Refresh();
    }

    public ObservableCollection<ManagerRow> Managers { get; } = [];
    public ObservableCollection<HistoryRow> History { get; } = [];

    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _nextRun = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _logText;
    [ObservableProperty] private string? _totalFreed;

    public string Version { get; } = "Sweepbox v" + (typeof(StatusViewModel).Assembly.GetName().Version?.ToString(3) ?? "?") + "  ·  GPL-3.0";

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void RunNow()
    {
        lock (_log)
        {
            _log.Clear();
            _logDirty = true;
        }
        _ = _services.RunNowAsync();
    }

    private bool CanRun() => !IsRunning;

    [RelayCommand]
    private void CancelRun() => _services.Engine.Cancel();

    [RelayCommand]
    private void OpenLogs() => Notifier.OpenPath(_services.Paths.LogDirectory);

    [RelayCommand]
    private void OpenLog(string? path)
    {
        if (path is not null && File.Exists(path))
            Notifier.OpenPath(path);
    }

    [RelayCommand]
    private void OpenSettings() => _showSettings();

    [RelayCommand]
    private void OpenLicense()
    {
        var license = Path.Combine(AppContext.BaseDirectory, "LICENSE.txt");
        Notifier.OpenPath(File.Exists(license) ? license : "https://www.gnu.org/licenses/gpl-3.0.html");
    }

    partial void OnIsRunningChanged(bool value) => RunNowCommand.NotifyCanExecuteChanged();

    private void OnOutput(string line)
    {
        lock (_log)
        {
            _log.AppendLine(line);
            if (_log.Length > MaxLogChars)
                _log.Remove(0, _log.Length - MaxLogChars);
            _logDirty = true;
        }
    }

    private void OnStatusChanged(EngineStatus _) => Dispatcher.UIThread.Post(Refresh);
    private void OnSchedulerChanged() => Dispatcher.UIThread.Post(Refresh);

    private void Tick()
    {
        lock (_log)
        {
            if (!_logDirty)
                return;
            LogText = _log.ToString();
            _logDirty = false;
        }
    }

    private void Refresh()
    {
        var status = _services.Engine.Status;
        var last = status.LastRun;
        IsRunning = status.IsRunning;

        if (status.IsRunning)
        {
            Headline = $"Running {status.CurrentManager ?? ""}".Trim();
            Detail = status.CurrentStep ?? "";
        }
        else if (last is null)
        {
            Headline = "No runs yet";
            Detail = "Click Run Now or wait for the schedule.";
        }
        else
        {
            Headline = last.Outcome switch
            {
                Outcome.Succeeded => "Everything is up to date",
                Outcome.Failed => "Last run had problems",
                Outcome.Cancelled => "Last run was cancelled",
                _ => "Nothing was run",
            };
            Detail = $"{Format.When(last.StartedAt)} · {Format.Duration(last.FinishedAt - last.StartedAt)} · {last.Trigger}"
                     + (last.BytesFreed is { } freed ? $" · {RunReport.FreedText(freed)}" : "");
        }

        var next = _services.Scheduler.NextRunAt;
        NextRun = _services.Scheduler.Paused ? "Schedule paused"
            : next is null ? "Schedule off"
            : $"Next run {Format.Relative(next.Value)}";

        Managers.Clear();
        foreach (var m in last?.Managers ?? [])
            Managers.Add(new ManagerRow(
                m.DisplayName,
                Format.Outcome(m.Outcome),
                m.Outcome == Outcome.Succeeded,
                m.Outcome == Outcome.Failed,
                m.Message ?? string.Join(" · ", m.Steps.Select(s => $"{s.Description} ({s.DurationSeconds:0}s)")),
                m.BytesFreed is { } freed ? RunReport.FreedText(freed) : null));

        History.Clear();
        foreach (var r in _services.History.Runs.Reverse().Take(15))
            History.Add(new HistoryRow(
                Format.When(r.StartedAt), r.Trigger.ToString(), Format.Outcome(r.Outcome), r.Outcome == Outcome.Failed,
                Format.Duration(r.FinishedAt - r.StartedAt),
                r.BytesFreed > 0 ? Core.Running.DiskUsage.Format(r.BytesFreed.Value) : "", r.LogFile));

        TotalFreed = Format.TotalFreed(_services.History.Runs);
    }

    public void Dispose()
    {
        _timer.Stop();
        _services.Engine.Output -= OnOutput;
        _services.Engine.StatusChanged -= OnStatusChanged;
        _services.Scheduler.Changed -= OnSchedulerChanged;
    }
}
