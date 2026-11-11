using Sweepbox.Core.Settings;

namespace Sweepbox.Core.Engine;

/// <summary>
/// Checks once a minute whether a run is due. Polling (rather than one long timer) means
/// sleep/wake and clock changes are handled naturally: an overdue run starts soon after wake.
/// </summary>
public sealed class Scheduler : IDisposable
{
    private readonly MaintenanceEngine _engine;
    private readonly Func<AppSettings> _settings;
    private readonly RunHistoryStore _history;
    private readonly TimeProvider _time;
    private readonly DateTimeOffset _appStartedAt;
    private ITimer? _timer;
    private bool _paused;

    public Scheduler(MaintenanceEngine engine, Func<AppSettings> settings, RunHistoryStore history, TimeProvider? time = null)
    {
        _engine = engine;
        _settings = settings;
        _history = history;
        _time = time ?? TimeProvider.System;
        _appStartedAt = _time.GetLocalNow();
    }

    public event Action? Changed;

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            Changed?.Invoke();
        }
    }

    /// <summary>When the next scheduled run will start, or null if scheduling is off/paused.</summary>
    public DateTimeOffset? NextRunAt
    {
        get
        {
            var settings = _settings();
            if (!settings.ScheduleEnabled || Paused)
                return null;
            return ComputeNextRun(
                _history.Latest?.StartedAt,
                TimeSpan.FromHours(settings.IntervalHours),
                _appStartedAt,
                TimeSpan.FromMinutes(settings.StartupDelayMinutes));
        }
    }

    public static DateTimeOffset ComputeNextRun(DateTimeOffset? lastRunStartedAt, TimeSpan interval, DateTimeOffset appStartedAt, TimeSpan startupDelay)
    {
        var earliest = appStartedAt + startupDelay;
        if (lastRunStartedAt is null)
            return earliest;
        var due = lastRunStartedAt.Value + interval;
        return due > earliest ? due : earliest;
    }

    public void Start()
    {
        _timer ??= _time.CreateTimer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1));
    }

    /// <summary>Starts a run if one is due. Returns the started run's task, or null.</summary>
    public Task<RunReport?>? TickAsync()
    {
        var next = NextRunAt;
        if (next is null || _engine.Status.IsRunning || _time.GetLocalNow() < next.Value)
            return null;
        var task = _engine.RunAsync(RunTrigger.Scheduled);
        task.ContinueWith(_ => Changed?.Invoke(), TaskScheduler.Default);
        return task;
    }

    public void Dispose() => _timer?.Dispose();
}
