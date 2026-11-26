using Microsoft.Extensions.Time.Testing;
using Sweepbox.Core.Engine;
using Sweepbox.Core.Managers;
using Sweepbox.Core.Running;
using Sweepbox.Core.Settings;

namespace Sweepbox.Tests;

/// <summary>Records commands and returns scripted exit codes; can block to simulate a long run.</summary>
internal sealed class FakeRunner : ICommandRunner
{
    public HashSet<string> Installed { get; } = ["brew", "npm", "choco"];
    public List<CommandSpec> Ran { get; } = [];
    public List<CommandSpec> Probes { get; } = [];
    public Func<CommandSpec, int> ExitCode { get; set; } = _ => 0;
    public TaskCompletionSource? Gate { get; set; }
    public Action<CommandSpec>? OnRun { get; set; }

    /// <summary>Answer for lookups like "brew --cache" (keyed by joined arguments).</summary>
    public Dictionary<string, string> ProbeAnswers { get; } = [];

    /// <summary>Exit code for a probe (keyed by joined arguments); 0 when absent.</summary>
    public Dictionary<string, int> ProbeExitCodes { get; } = [];

    /// <summary>Probes (keyed by joined arguments) that report a timeout.</summary>
    public HashSet<string> ProbeTimeouts { get; } = [];

    public string? ResolveExecutable(string name) => Installed.Contains(name) ? "/fake/" + name : null;

    public async Task<CommandResult> RunAsync(CommandSpec spec, Action<string> onOutput, TimeSpan timeout, CancellationToken ct)
    {
        if (spec.IsProbe)
        {
            Probes.Add(spec);
            var key = string.Join(' ', spec.Arguments);
            if (ProbeAnswers.TryGetValue(key, out var answer))
                onOutput(answer);
            return ProbeTimeouts.Contains(key)
                ? new CommandResult(-1, TimeSpan.Zero, TimedOut: true)
                : new CommandResult(ProbeExitCodes.GetValueOrDefault(key, 0), TimeSpan.Zero);
        }
        Ran.Add(spec);
        OnRun?.Invoke(spec);
        onOutput("output of " + spec.CommandLine);
        if (Gate is not null)
        {
            try { await Gate.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { return new CommandResult(-1, TimeSpan.Zero, Cancelled: true); }
        }
        return new CommandResult(ExitCode(spec), TimeSpan.FromSeconds(1));
    }
}

public sealed class EngineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sweepbox-tests-").FullName;
    private readonly FakeRunner _runner = new();
    private readonly AppSettings _settings = new();
    private readonly RunHistoryStore _history;

    public EngineTests() => _history = new RunHistoryStore(Path.Combine(_dir, "history.json"));

    private MaintenanceEngine Engine(TimeProvider? time = null) => new(
        () => _settings,
        _ => Task.FromResult<ICommandRunner>(_runner),
        [new BrewManager(), new NpmManager()],
        _history,
        Path.Combine(_dir, "logs"),
        time);

    [Fact]
    public async Task Runs_every_enabled_manager_and_records_history()
    {
        var report = await Engine().RunAsync(RunTrigger.Manual);

        Assert.NotNull(report);
        Assert.Equal(Outcome.Succeeded, report.Outcome);
        Assert.Equal(["brew", "npm"], report.Managers.Select(m => m.Id));
        Assert.Equal(7, _runner.Ran.Count);
        Assert.Single(_history.Runs);
        Assert.True(File.Exists(report.LogFile));
        Assert.Contains("output of brew update", File.ReadAllText(report.LogFile!));
    }

    [Fact]
    public async Task Missing_tool_is_skipped_not_failed()
    {
        _runner.Installed.Remove("brew");

        var report = await Engine().RunAsync(RunTrigger.Manual);

        var brew = report!.Managers.Single(m => m.Id == "brew");
        Assert.Equal(Outcome.Skipped, brew.Outcome);
        Assert.Contains("not found", brew.Message);
        Assert.Equal(Outcome.Succeeded, report.Outcome);
    }

    [Fact]
    public async Task A_failing_step_marks_manager_failed_but_later_steps_and_managers_still_run()
    {
        _runner.ExitCode = spec => spec.Arguments.SequenceEqual(["update"]) ? 1 : 0;

        var report = await Engine().RunAsync(RunTrigger.Scheduled);

        Assert.Equal(Outcome.Failed, report!.Outcome);
        var brew = report.Managers.Single(m => m.Id == "brew");
        Assert.Equal(Outcome.Failed, brew.Outcome);
        Assert.Equal("Update Homebrew", brew.Message);
        Assert.Equal(5, brew.Steps.Count);
        Assert.Equal(Outcome.Succeeded, report.Managers.Single(m => m.Id == "npm").Outcome);
    }

    [Fact]
    public async Task Disabled_manager_is_not_run_unless_explicitly_requested()
    {
        _settings.Brew.Enabled = false;
        var engine = Engine();

        var scheduled = await engine.RunAsync(RunTrigger.Scheduled);
        Assert.Equal(["npm"], scheduled!.Managers.Select(m => m.Id));

        var only = await engine.RunAsync(RunTrigger.Manual, ["brew"]);
        Assert.Equal(["brew"], only!.Managers.Select(m => m.Id));
    }

    [Fact]
    public async Task Second_run_while_running_is_rejected_and_cancel_stops_the_run()
    {
        _runner.Gate = new TaskCompletionSource();
        var engine = Engine();

        var first = engine.RunAsync(RunTrigger.Manual);
        await WaitUntil(() => engine.Status.IsRunning && _runner.Ran.Count == 1);

        Assert.Null(await engine.RunAsync(RunTrigger.Manual));

        engine.Cancel();
        var report = await first;
        Assert.Equal(Outcome.Cancelled, report!.Outcome);
        Assert.Single(_runner.Ran);
        Assert.False(engine.Status.IsRunning);
    }

    [Fact]
    public async Task Uninstall_runs_one_step_per_package_and_continues_after_a_failure()
    {
        _runner.ExitCode = s => s.Arguments.Contains("zoom") ? 1 : 0;
        var report = await Engine().UninstallAsync([
            new("brew", "zoom", "6.2.11", PackageKind.Cask), new("brew", "wget", "1.25.0", PackageKind.Formula),
            new("npm", "typescript", "5.6.3", PackageKind.Package)]);
        Assert.Equal(RunTrigger.Uninstall, report!.Trigger);
        Assert.Equal(["Uninstall zoom", "Uninstall wget"], report.Managers[0].Steps.Select(s => s.Description));
        Assert.Equal(Outcome.Failed, report.Managers[0].Outcome);
        Assert.Equal(Outcome.Succeeded, report.Managers[1].Outcome);
        Assert.Null(report.BytesFreed);
        Assert.Equal(3, _runner.Ran.Count);
    }

    [Fact]
    public async Task Uninstall_returns_null_while_another_run_is_in_progress()
    {
        _runner.Gate = new();
        var engine = Engine();
        var running = engine.RunAsync(RunTrigger.Manual);
        Assert.Null(await engine.UninstallAsync([new("npm", "typescript", null, PackageKind.Package)]));
        _runner.Gate.SetResult();
        await running;
    }

    [Fact]
    public async Task Uninstall_never_runs_protected_packages()
    {
        await Engine().UninstallAsync([new("npm", "npm", null, PackageKind.Package), new("npm", "typescript", null, PackageKind.Package)]);
        Assert.Equal(["uninstall --global typescript"], _runner.Ran.Select(s => string.Join(' ', s.Arguments)));
    }

    [Fact]
    public async Task Uninstall_does_not_replace_last_run_or_move_the_schedule()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero));
        var engine = Engine(time);
        var maintenance = await engine.RunAsync(RunTrigger.Scheduled);
        time.Advance(TimeSpan.FromHours(5));
        await engine.UninstallAsync([new("npm", "typescript", null, PackageKind.Package)]);
        Assert.Same(maintenance, engine.Status.LastRun);
        Assert.Equal(maintenance!.StartedAt, _history.LatestMaintenance!.StartedAt);
        Assert.Equal(RunTrigger.Uninstall, _history.Latest!.Trigger);
        var scheduler = new Scheduler(engine, () => _settings, _history, time);
        Assert.Equal(maintenance.StartedAt + TimeSpan.FromHours(24), scheduler.NextRunAt);
    }

    [Fact]
    public async Task Cancelling_an_uninstall_marks_remaining_managers_cancelled()
    {
        _runner.Gate = new TaskCompletionSource();
        var engine = Engine();

        var running = engine.UninstallAsync([
            new("brew", "wget", null, PackageKind.Formula), new("npm", "typescript", null, PackageKind.Package)]);
        await WaitUntil(() => engine.Status.IsRunning && _runner.Ran.Count == 1);

        engine.Cancel();
        var report = await running;
        Assert.Equal(Outcome.Cancelled, report!.Outcome);
        Assert.Equal(Outcome.Cancelled, report.Managers[1].Outcome);
        Assert.Single(_runner.Ran);
    }

    [Fact]
    public async Task Uninstall_removes_companions_right_after_their_package()
    {
        var report = await Engine().UninstallAsync([
            new("npm", "git", "2.47.1", PackageKind.Package, ["git.install", "git.portable"]),
            new("npm", "typescript", null, PackageKind.Package)]);
        Assert.Equal(["Uninstall git", "Uninstall git.install", "Uninstall git.portable", "Uninstall typescript"],
            report!.Managers.Single().Steps.Select(s => s.Description));
        Assert.Equal(["git", "git.install", "git.portable", "typescript"], _runner.Ran.Select(s => s.Arguments[^1]));
    }

    [Fact]
    public async Task Uninstall_never_runs_a_protected_companion()
    {
        await Engine().UninstallAsync([new("npm", "tool", null, PackageKind.Package, ["corepack"])]);
        Assert.Equal(["uninstall --global tool"], _runner.Ran.Select(s => string.Join(' ', s.Arguments)));
    }

    [Fact]
    public async Task Uninstall_calls_on_started_before_the_first_log_line()
    {
        var events = new List<string>();
        var engine = Engine();
        engine.Output += line => { lock (events) events.Add("output"); };
        await engine.UninstallAsync([new("npm", "typescript", null, PackageKind.Package)], () => { lock (events) events.Add("started"); });
        Assert.Equal("started", events[0]);
        Assert.Equal(1, events.Count(e => e == "started"));
    }

    [Fact]
    public async Task Uninstall_does_not_call_on_started_when_another_run_is_in_progress()
    {
        _runner.Gate = new();
        var engine = Engine();
        var running = engine.RunAsync(RunTrigger.Manual);
        var started = false;
        Assert.Null(await engine.UninstallAsync([new("npm", "typescript", null, PackageKind.Package)], () => started = true));
        _runner.Gate.SetResult();
        await running;
        Assert.False(started);
    }

    [Fact]
    public async Task A_new_engine_ignores_a_newest_uninstall_run_for_last_run()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero));
        var maintenance = await Engine(time).RunAsync(RunTrigger.Scheduled);
        time.Advance(TimeSpan.FromHours(1));
        await Engine(time).UninstallAsync([new("npm", "typescript", null, PackageKind.Package)]);
        Assert.Equal(RunTrigger.Uninstall, _history.Latest!.Trigger);

        var fresh = Engine(time);
        Assert.NotNull(fresh.Status.LastRun);
        Assert.Equal(RunTrigger.Scheduled, fresh.Status.LastRun!.Trigger);
        Assert.Equal(maintenance!.StartedAt, fresh.Status.LastRun.StartedAt);
    }

    [Fact]
    public async Task Uninstall_rejects_an_empty_list()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Engine().UninstallAsync([]));
    }

    [Fact]
    public async Task Scheduler_waits_for_startup_delay_then_interval()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero));
        _settings.IntervalHours = 6;
        _settings.StartupDelayMinutes = 5;
        var engine = Engine(time);
        using var scheduler = new Scheduler(engine, () => _settings, _history, time);

        Assert.Null(scheduler.TickAsync());                    // within startup delay
        time.Advance(TimeSpan.FromMinutes(5));
        var run = scheduler.TickAsync();
        Assert.NotNull(run);
        await run;
        Assert.Equal(time.GetUtcNow().AddHours(6), scheduler.NextRunAt);

        time.Advance(TimeSpan.FromHours(5));
        Assert.Null(scheduler.TickAsync());
        time.Advance(TimeSpan.FromHours(1));
        Assert.NotNull(scheduler.TickAsync());
    }

    [Fact]
    public void Scheduler_respects_pause_and_disabled_schedule()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        _settings.StartupDelayMinutes = 0;
        using var scheduler = new Scheduler(Engine(time), () => _settings, _history, time);

        scheduler.Paused = true;
        Assert.Null(scheduler.NextRunAt);
        Assert.Null(scheduler.TickAsync());

        scheduler.Paused = false;
        _settings.ScheduleEnabled = false;
        Assert.Null(scheduler.TickAsync());
    }

    [Theory]
    [InlineData(null, 10, "2026-01-01T00:10:00Z")]               // never run: after startup delay
    [InlineData("2025-12-31T00:00:00Z", 10, "2026-01-01T00:10:00Z")] // long overdue: after startup delay
    [InlineData("2025-12-31T22:00:00Z", 10, "2026-01-01T22:00:00Z")] // not yet due: last + 24h
    public void ComputeNextRun(string? last, int delayMinutes, string expected)
    {
        var next = Scheduler.ComputeNextRun(
            last is null ? null : DateTimeOffset.Parse(last),
            TimeSpan.FromHours(24),
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            TimeSpan.FromMinutes(delayMinutes));
        Assert.Equal(DateTimeOffset.Parse(expected), next);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task Measures_space_freed_by_cleanup_steps_only()
    {
        var cache = Directory.CreateDirectory(Path.Combine(_dir, "npm-cache")).FullName;
        File.WriteAllBytes(Path.Combine(cache, "a"), new byte[3000]);
        File.WriteAllBytes(Path.Combine(cache, "b"), new byte[2000]);
        _runner.ProbeAnswers["config get cache"] = cache;
        _settings.Brew.Enabled = false;
        _runner.OnRun = spec =>
        {
            // The upgrade adds data (must not count); the cache cleanup removes 3000 bytes.
            if (spec.Arguments[0] == "update") File.WriteAllBytes(Path.Combine(cache, "new"), new byte[500]);
            if (spec.IsCleanup) File.Delete(Path.Combine(cache, "a"));
        };

        var report = await Engine().RunAsync(RunTrigger.Manual);

        var npm = report!.Managers.Single();
        Assert.Equal(3000, npm.BytesFreed);
        Assert.Equal(3000, report.BytesFreed);
        Assert.Contains("Freed 3 KB", report.Summary);
        Assert.Contains("Space freed: 3 KB", File.ReadAllText(report.LogFile!));
    }

    [Fact]
    public async Task No_freed_space_reported_when_cleanup_is_disabled()
    {
        _settings.Brew.Autoremove = false;
        _settings.Brew.Cleanup = false;
        _settings.Npm.CacheMode = NpmCacheMode.None;

        var report = await Engine().RunAsync(RunTrigger.Manual);

        Assert.Empty(_runner.Probes);
        Assert.All(report!.Managers, m => Assert.Null(m.BytesFreed));
        Assert.Null(report.BytesFreed);
        Assert.DoesNotContain("Freed", report.Summary);
    }

    [Fact]
    public async Task Brew_cleanup_locations_come_from_brew_itself()
    {
        _runner.ProbeAnswers["--cache"] = "/cache/Homebrew";
        _runner.ProbeAnswers["--prefix"] = "/opt/homebrew";

        var locations = await new BrewManager().GetCleanupLocationsAsync(_runner, "brew", CancellationToken.None);

        Assert.Equal(["/cache/Homebrew", Path.Combine("/opt/homebrew", "Cellar"), Path.Combine("/opt/homebrew", "Caskroom")], locations);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
