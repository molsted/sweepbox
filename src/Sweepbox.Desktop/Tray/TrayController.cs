using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Sweepbox.Core.Engine;
using Sweepbox.Desktop.Services;

namespace Sweepbox.Desktop.Tray;

/// <summary>The menu bar item (macOS) / notification area icon (Windows) and its menu.</summary>
public sealed class TrayController : IDisposable
{
    private readonly AppServices _services;
    private readonly TrayIcon _tray;
    private readonly NativeMenuItem _header = new() { IsEnabled = false };
    private readonly NativeMenuItem _lastRun = new() { IsEnabled = false };
    private readonly NativeMenuItem _nextRun = new() { IsEnabled = false };
    private readonly NativeMenuItem _freed = new() { IsEnabled = false, IsVisible = false };
    private readonly NativeMenuItem _runNow = new("Run Now");
    private readonly NativeMenuItem _runOnly = new("Run Only");
    private readonly NativeMenuItem _cancel = new("Cancel Run");
    private readonly NativeMenuItem _pause = new("Pause Schedule") { ToggleType = MenuItemToggleType.CheckBox };
    private readonly NativeMenuItem _checkUpdates = new("Check for Updates…");
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<string, WindowIcon> _icons = new();
    private string? _currentIconState;

    public TrayController(Application app, AppServices services, Action showStatus, Action showSettings, Action checkUpdates, Action quit)
    {
        _services = services;

        _runNow.Click += (_, _) => _ = services.RunNowAsync();
        _cancel.Click += (_, _) => services.Engine.Cancel();
        _pause.Click += (_, _) => services.Scheduler.Paused = !services.Scheduler.Paused;

        var runOnlyMenu = new NativeMenu();
        foreach (var manager in services.Engine.Managers)
        {
            var item = new NativeMenuItem(manager.DisplayName);
            var id = manager.Id;
            item.Click += (_, _) => _ = services.RunNowAsync(id);
            runOnlyMenu.Items.Add(item);
        }
        _runOnly.Menu = runOnlyMenu;

        _checkUpdates.Click += (_, _) => checkUpdates();

        var status = new NativeMenuItem("Status and Log…");
        status.Click += (_, _) => showStatus();
        var settings = new NativeMenuItem("Settings…");
        settings.Click += (_, _) => showSettings();
        var quitItem = new NativeMenuItem("Quit Sweepbox");
        quitItem.Click += (_, _) => quit();

        var menu = new NativeMenu();
        foreach (var item in new NativeMenuItemBase[]
                 {
                     _header, _lastRun, _nextRun, _freed, new NativeMenuItemSeparator(),
                     _runNow, _runOnly, _cancel, new NativeMenuItemSeparator(),
                     _pause, status, settings, new NativeMenuItemSeparator(),
                     _checkUpdates, new NativeMenuItemSeparator(),
                     quitItem,
                 })
            menu.Items.Add(item);

        // The macOS native backend throws on a null ToolTipText when the icon is attached; Refresh() sets the real text.
        _tray = new TrayIcon { Menu = menu, IsVisible = true, ToolTipText = "Sweepbox" };
        if (OperatingSystem.IsMacOS())
            MacOSProperties.SetIsTemplateIcon(_tray, true);
        else
            _tray.Clicked += (_, _) => showStatus(); // Windows: left click opens status, right click the menu.

        TrayIcon.SetIcons(app, [_tray]);

        services.Engine.StatusChanged += _ => Dispatcher.UIThread.Post(Refresh);
        services.Scheduler.Changed += () => Dispatcher.UIThread.Post(Refresh);
        services.Settings.Changed += _ => Dispatcher.UIThread.Post(Refresh);
        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => Refresh());
        _refreshTimer.Start();
        Refresh();
    }

    private void Refresh()
    {
        var status = _services.Engine.Status;
        var last = status.LastRun;
        var next = _services.Scheduler.NextRunAt;

        _header.Header = status.IsRunning
            ? $"Running: {status.CurrentManager ?? "starting"} – {status.CurrentStep}"
            : "Sweepbox – Idle";
        _lastRun.Header = last is null
            ? "Last run: never"
            : $"Last run: {Format.When(last.StartedAt)} – {Format.Outcome(last.Outcome)}"
              + (last.BytesFreed > 0 ? $", freed {Core.Running.DiskUsage.Format(last.BytesFreed.Value)}" : "");

        // Only shown once a run with cleanup enabled has measured something.
        var total = Format.TotalFreed(_services.History.Runs);
        _freed.IsVisible = total is not null;
        _freed.Header = total ?? "";
        _nextRun.Header = _services.Scheduler.Paused ? "Next run: paused"
            : next is null ? "Next run: schedule off"
            : $"Next run: {Format.Relative(next.Value)}";

        _runNow.IsEnabled = !status.IsRunning;
        _runOnly.IsEnabled = !status.IsRunning;
        _cancel.IsEnabled = status.IsRunning;
        _pause.IsChecked = _services.Scheduler.Paused;

        var state = status.IsRunning ? "running" : last?.Outcome == Outcome.Failed ? "error" : "idle";
        if (state != _currentIconState)
        {
            _tray.Icon = LoadIcon(state);
            _currentIconState = state;
        }
        _tray.ToolTipText = status.IsRunning ? $"Sweepbox – {status.CurrentStep}" : $"Sweepbox – {_lastRun.Header}";
    }

    private WindowIcon LoadIcon(string state)
    {
        var platform = OperatingSystem.IsMacOS() ? "mac" : "win";
        var key = $"tray-{platform}-{state}";
        if (!_icons.TryGetValue(key, out var icon))
        {
            using var stream = AssetLoader.Open(new Uri($"avares://Sweepbox/Assets/{key}.png"));
            icon = new WindowIcon(stream);
            _icons[key] = icon;
        }
        return icon;
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _tray.IsVisible = false;
        _tray.Dispose();
    }
}
