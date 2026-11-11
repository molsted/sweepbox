using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = new AppServices(Paths);
            _tray = new TrayController(this, _services, ShowStatus, ShowSettings, () => Quit(desktop));
            _services.Start();
            desktop.Exit += (_, _) => _services.Dispose();

            // First launch: open settings so the user sees what will run.
            if (!File.Exists(Paths.SettingsFile))
            {
                var initial = _services.Settings.Current;
                initial.LaunchAtLogin = IsInstalledCopy();
                _services.Settings.Save(initial);
                ShowSettings();
            }
        }

        base.OnFrameworkInitializationCompleted();
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
        _tray?.Dispose();
        desktop.Shutdown();
    }
}
