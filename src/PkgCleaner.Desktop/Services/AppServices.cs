using PkgCleaner.Core.Engine;
using PkgCleaner.Core.Managers;
using PkgCleaner.Core.Platform;
using PkgCleaner.Core.Running;
using PkgCleaner.Core.Settings;

namespace PkgCleaner.Desktop.Services;

/// <summary>Wires the core pieces together; one instance for the app's lifetime.</summary>
public sealed class AppServices : IDisposable
{
    public AppServices(AppPaths paths)
    {
        Paths = paths;
        Settings = new SettingsStore(paths.SettingsFile);
        History = new RunHistoryStore(paths.HistoryFile);
        Engine = new MaintenanceEngine(
            () => Settings.Current,
            async settings => new ProcessCommandRunner(await ShellEnvironment.ResolvePathAsync(settings.ExtraPath)),
            PackageManager.ForThisOS,
            History,
            paths.LogDirectory);
        Scheduler = new Scheduler(Engine, () => Settings.Current, History);

        Engine.RunCompleted += Notify;
        Settings.Changed += s => TryApplyLaunchAtLogin(s.LaunchAtLogin);
    }

    public AppPaths Paths { get; }
    public SettingsStore Settings { get; }
    public RunHistoryStore History { get; }
    public MaintenanceEngine Engine { get; }
    public Scheduler Scheduler { get; }

    public void Start()
    {
        // Keep the login item pointing at the current executable (it may have moved after an update).
        TryApplyLaunchAtLogin(Settings.Current.LaunchAtLogin);
        Scheduler.Start();
    }

    public Task RunNowAsync(params string[] onlyManagerIds) =>
        Engine.RunAsync(RunTrigger.Manual, onlyManagerIds.Length == 0 ? null : onlyManagerIds);

    private void Notify(RunReport report)
    {
        var settings = Settings.Current;
        var failed = report.Outcome == Outcome.Failed;
        if ((failed && settings.NotifyOnFailure) || (report.Outcome == Outcome.Succeeded && settings.NotifyOnSuccess))
            Notifier.Show(failed ? "Maintenance failed" : "Maintenance finished", report.Summary);
    }

    private static void TryApplyLaunchAtLogin(bool enabled)
    {
        try
        {
            LaunchAtLogin.Apply(enabled);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Could not update login item: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Engine.Cancel();
        Scheduler.Dispose();
    }
}
