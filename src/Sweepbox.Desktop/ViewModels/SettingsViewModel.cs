using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sweepbox.Core.Managers;
using Sweepbox.Core.Settings;

namespace Sweepbox.Desktop.ViewModels;

/// <summary>Edits a copy of the settings; nothing is applied until Save.</summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsStore _store;

    public SettingsViewModel(SettingsStore store, IReadOnlyList<PackageManager> availableManagers)
    {
        _store = store;
        Settings = store.Current;
        ShowBrew = availableManagers.Any(m => m is BrewManager);
        ShowNpm = availableManagers.Any(m => m is NpmManager);
        ShowChoco = availableManagers.Any(m => m is ChocoManager);
        Subtitle = $"Keep {PackageManager.JoinNames(availableManagers)} fresh and tidy.";
        _intervalHours = Settings.IntervalHours;
        _startupDelayMinutes = Settings.StartupDelayMinutes;
        _stepTimeoutMinutes = Settings.StepTimeoutMinutes;
        _npmCacheModeIndex = (int)Settings.Npm.CacheMode;
    }

    public event Action? CloseRequested;

    /// <summary>The working copy; check boxes bind straight to it.</summary>
    public AppSettings Settings { get; }

    public bool ShowBrew { get; }
    public bool ShowNpm { get; }
    public bool ShowChoco { get; }

    /// <summary>Names only the package managers available on this OS.</summary>
    public string Subtitle { get; }

    public string SettingsFolder => Path.GetDirectoryName(App.Paths.SettingsFile)!;

    public IReadOnlyList<string> NpmCacheModes { get; } =
    [
        "Leave cache alone",
        "Verify (garbage-collect unused data)",
        "Clear completely (npm cache clean --force)",
    ];

    public IReadOnlyList<int> IntervalPresets { get; } = [1, 3, 6, 12, 24, 48, 72, 168];

    [ObservableProperty] private decimal _intervalHours;
    [ObservableProperty] private decimal _startupDelayMinutes;
    [ObservableProperty] private decimal _stepTimeoutMinutes;
    [ObservableProperty] private int _npmCacheModeIndex;

    public string IntervalDescription => IntervalHours switch
    {
        24 => "Once a day",
        168 => "Once a week",
        < 24 => $"Every {IntervalHours:0} hours",
        _ => $"Every {IntervalHours / 24:0.#} days",
    };

    partial void OnIntervalHoursChanged(decimal value) => OnPropertyChanged(nameof(IntervalDescription));

    [RelayCommand]
    private void Save()
    {
        Settings.IntervalHours = (int)IntervalHours;
        Settings.StartupDelayMinutes = (int)StartupDelayMinutes;
        Settings.StepTimeoutMinutes = (int)StepTimeoutMinutes;
        Settings.Npm.CacheMode = (NpmCacheMode)NpmCacheModeIndex;
        _store.Save(Settings);
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    [RelayCommand]
    private void OpenSettingsFolder() => Services.Notifier.OpenPath(SettingsFolder);
}
