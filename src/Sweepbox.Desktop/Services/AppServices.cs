using Sweepbox.Core.Engine;
using Sweepbox.Core.Managers;
using Sweepbox.Core.Platform;
using Sweepbox.Core.Running;
using Sweepbox.Core.Settings;

namespace Sweepbox.Desktop.Services;

/// <summary>Wires the core pieces together; one instance for the app's lifetime.</summary>
public sealed class AppServices : IDisposable
{
    public AppServices(
        AppPaths paths,
        IReadOnlyList<PackageManager>? managers = null,
        Func<AppSettings, Task<ICommandRunner>>? runnerFactory = null)
    {
        Paths = paths;
        Settings = new SettingsStore(paths.SettingsFile);
        History = new RunHistoryStore(paths.HistoryFile);
        managers ??= PackageManager.ForThisOS;
        runnerFactory ??= async settings => new ProcessCommandRunner(await ShellEnvironment.ResolvePathAsync(settings.ExtraPath));
        Engine = new MaintenanceEngine(() => Settings.Current, runnerFactory, managers, History, paths.LogDirectory);
        Inventory = new PackageInventory(() => Settings.Current, runnerFactory, managers);
        Scheduler = new Scheduler(Engine, () => Settings.Current, History);

        Engine.RunCompleted += Notify;
        Settings.Changed += s => TryApplyLaunchAtLogin(s.LaunchAtLogin);
    }

    public AppPaths Paths { get; }
    public SettingsStore Settings { get; }
    public RunHistoryStore History { get; }
    public MaintenanceEngine Engine { get; }
    public Scheduler Scheduler { get; }
    public PackageInventory Inventory { get; }

    public void Start()
    {
        // Keep the login item pointing at the current executable (it may have moved after an update).
        // Not when the settings file couldn't be read: the defaults would remove the user's login item.
        if (!Settings.LoadFailed)
            TryApplyLaunchAtLogin(Settings.Current.LaunchAtLogin);
        Scheduler.Start();
    }

    public Task RunNowAsync(params string[] onlyManagerIds) =>
        Engine.RunAsync(RunTrigger.Manual, onlyManagerIds.Length == 0 ? null : onlyManagerIds);

    /// <summary>Uninstalls the packages as one run; <c>null</c> when another run is in progress.</summary>
    /// <param name="onStarted">Called once the run is accepted, before its first log line.</param>
    public Task<RunReport?> UninstallAsync(IReadOnlyList<InstalledPackage> packages, Action? onStarted = null) =>
        Engine.UninstallAsync(packages, onStarted);

    private void Notify(RunReport report)
    {
        var settings = Settings.Current;
        var failed = report.Outcome == Outcome.Failed;
        if (!((failed && settings.NotifyOnFailure) || (report.Outcome == Outcome.Succeeded && settings.NotifyOnSuccess)))
            return;
        var uninstall = report.Trigger == RunTrigger.Uninstall;
        var title = uninstall
            ? failed ? "Uninstall failed" : "Uninstall finished"
            : failed ? "Maintenance failed" : "Maintenance finished";
        Notifier.Show(title, uninstall ? UninstallBody(report) : report.Summary);
    }

    /// <summary>The failed packages (each manager's failure message), or the summary when nothing failed.</summary>
    private static string UninstallBody(RunReport report)
    {
        var failures = report.Managers
            .Where(m => m.Outcome == Outcome.Failed && !string.IsNullOrEmpty(m.Message))
            .Select(m => m.Message!)
            .ToList();
        return failures.Count > 0 ? string.Join("; ", failures) : report.Summary;
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
