using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Sweepbox.Core.Settings;
using Sweepbox.Desktop.Services;
using Sweepbox.Desktop.Tray;
using Sweepbox.Desktop.ViewModels;
using Sweepbox.Desktop.Views;

namespace Sweepbox.Desktop;

public partial class App : Application
{
    internal static AppPaths Paths { get; set; } = AppPaths.Default();

    private AppServices? _services;
    private TrayController? _tray;
    private StatusWindow? _statusWindow;
    private SettingsWindow? _settingsWindow;
    private AppUpdater? _updater;
    private bool _quitting;
    private DateTime _lastErrorNotice = DateTime.MinValue;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (OperatingSystem.IsWindows())
                WindowsNotifications.Register();
            else if (OperatingSystem.IsMacOS())
                MacNotifications.Initialize(requestPermission: true);
            // A failure in a click or binding handler is logged and the tray app keeps running.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Trace.WriteLine($"Unhandled UI exception: {e.Exception}");
                // The process can't safely continue after these; let them end it as before.
                if (e.Exception is OutOfMemoryException or InvalidProgramException or AccessViolationException)
                    return;
                e.Handled = true;
                // Say so (at most once a minute), so a failed action like Save isn't silently lost.
                if (DateTime.UtcNow - _lastErrorNotice > TimeSpan.FromMinutes(1))
                {
                    _lastErrorNotice = DateTime.UtcNow;
                    Notifier.Show("Something went wrong", $"{e.Exception.Message} Details are in sweepbox.log in the log folder.");
                }
            };
            _services = new AppServices(Paths);
            if (_services.Settings.LoadFailed)
                Notifier.Show("Settings couldn't be read",
                    "Sweepbox is using default settings until you save them again. Details are in sweepbox.log in the log folder.");
            // Quit can be requested from background threads (the updater installs off the UI thread);
            // disposing the tray icon anywhere but the UI thread hangs on macOS, so always quit there.
            var quit = () => Dispatcher.UIThread.Post(() => Quit(desktop));
            _updater = new AppUpdater(_services.Settings, LoadAppIcon(), quit);
            _tray = new TrayController(this, _services, ShowStatus, ShowSettings, _updater.CheckAtUserRequest, quit);
            _services.Settings.Changed += _ => _updater.UpdateAutomatic();
            _services.Start();
            _updater.UpdateAutomatic();
            desktop.Exit += (_, _) => _services.Dispose();

            // First launch: ask about starting at login, then open settings so the user sees what will run.
            if (!File.Exists(Paths.SettingsFile))
            {
                var initial = _services.Settings.Current;
                _services.Settings.Save(initial);
                ShowSettings();
                if (IsInstalledCopy())
                    _ = AskStartAtLoginOnFirstRunAsync();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Asks the start-at-login question once; the saved settings file means it is never asked again.</summary>
    private async Task AskStartAtLoginOnFirstRunAsync()
    {
        if (_settingsWindow is null)
            return;
        var start = await new FirstRunDialog().ShowDialog<bool>(_settingsWindow);
        var settings = _services!.Settings.Current;
        settings.LaunchAtLogin = start;
        _services.Settings.Save(settings);
        // Keep the open settings window's working copy in sync so a later Save does not undo the answer.
        if (_settingsWindow?.DataContext is SettingsViewModel vm)
            vm.Settings.LaunchAtLogin = start;
    }

    private void ShowStatus()
    {
        if (_statusWindow is null)
        {
            _statusWindow = new StatusWindow { DataContext = new StatusViewModel(_services!, ShowSettings) };
            _statusWindow.Closed += (_, _) =>
            {
                (_statusWindow?.DataContext as IDisposable)?.Dispose();
                _statusWindow = null;
            };
            _statusWindow.Show();
        }
        BringToFront(_statusWindow);
    }

    private void ShowSettings()
    {
        if (_settingsWindow is null)
        {
            var vm = new SettingsViewModel(_services!.Settings, _services.Engine.Managers);
            _settingsWindow = new SettingsWindow { DataContext = vm };
            vm.CloseRequested += () => _settingsWindow?.Close();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        BringToFront(_settingsWindow);
    }

    private static WindowIcon LoadAppIcon()
        => new(AssetLoader.Open(new Uri("avares://Sweepbox/Assets/app-icon.png")));

    /// <summary>True for an installed app (.app bundle / installer), false for a dev build in bin/.</summary>
    private static bool IsInstalledCopy()
    {
        var exe = Environment.ProcessPath ?? "";
        if (OperatingSystem.IsMacOS())
            return exe.Contains(".app/Contents/MacOS/", StringComparison.Ordinal);
        var sep = Path.DirectorySeparatorChar;
        return !exe.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase);
    }

    private static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
        // Menu-bar-only apps on macOS are not frontmost; toggling Topmost pulls the window forward.
        window.Topmost = true;
        window.Topmost = false;
    }

    private void Quit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // The updater can ask twice (its install step and NetSparkle's CloseApplication); quit once.
        if (_quitting)
            return;
        _quitting = true;
        _tray?.Dispose();
        desktop.Shutdown();
    }
}
