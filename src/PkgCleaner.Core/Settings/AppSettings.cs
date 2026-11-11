namespace PkgCleaner.Core.Settings;

public enum NpmCacheMode
{
    None,
    Verify,
    Clean,
}

public sealed class AppSettings
{
    /// <summary>Hours between scheduled maintenance runs.</summary>
    public int IntervalHours { get; set; } = 24;

    /// <summary>When false, runs only happen when started manually.</summary>
    public bool ScheduleEnabled { get; set; } = true;

    /// <summary>Minutes to wait after app start before an overdue run may begin.</summary>
    public int StartupDelayMinutes { get; set; } = 5;

    public bool LaunchAtLogin { get; set; }

    public bool NotifyOnFailure { get; set; } = true;

    public bool NotifyOnSuccess { get; set; }

    /// <summary>Max minutes a single command may run before it is killed.</summary>
    public int StepTimeoutMinutes { get; set; } = 30;

    /// <summary>Extra directories prepended to PATH when locating tools (separated by the OS path separator).</summary>
    public string ExtraPath { get; set; } = "";

    public BrewSettings Brew { get; set; } = new();

    public NpmSettings Npm { get; set; } = new();

    public ChocoSettings Choco { get; set; } = new();

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.Brew = (BrewSettings)Brew.MemberwiseCloneInternal();
        copy.Npm = (NpmSettings)Npm.MemberwiseCloneInternal();
        copy.Choco = (ChocoSettings)Choco.MemberwiseCloneInternal();
        return copy;
    }

    /// <summary>Clamps values into sane ranges (e.g. after loading a hand-edited file).</summary>
    public AppSettings Normalize()
    {
        IntervalHours = Math.Clamp(IntervalHours, 1, 24 * 30);
        StartupDelayMinutes = Math.Clamp(StartupDelayMinutes, 0, 120);
        StepTimeoutMinutes = Math.Clamp(StepTimeoutMinutes, 1, 240);
        ExtraPath ??= "";
        Brew ??= new();
        Npm ??= new();
        Choco ??= new();
        Choco.Except ??= "";
        return this;
    }
}

public abstract class ManagerSettings
{
    public bool Enabled { get; set; } = true;

    internal object MemberwiseCloneInternal() => MemberwiseClone();
}

public sealed class BrewSettings : ManagerSettings
{
    public bool Update { get; set; } = true;
    public bool UpgradeFormulae { get; set; } = true;
    public bool UpgradeCasks { get; set; } = true;

    /// <summary>Also upgrade casks that auto-update themselves (--greedy).</summary>
    public bool Greedy { get; set; }

    public bool Autoremove { get; set; } = true;
    public bool Cleanup { get; set; } = true;

    /// <summary>Remove all cached downloads, not just old ones (--prune=all).</summary>
    public bool PruneAll { get; set; }
}

public sealed class NpmSettings : ManagerSettings
{
    public bool UpdateGlobals { get; set; } = true;
    public NpmCacheMode CacheMode { get; set; } = NpmCacheMode.Verify;
}

public sealed class ChocoSettings : ManagerSettings
{
    public bool UpgradeAll { get; set; } = true;

    /// <summary>Comma-separated package ids to leave alone.</summary>
    public string Except { get; set; } = "";

    public bool CacheRemove { get; set; } = true;

    /// <summary>When the app is not elevated, ask for a UAC prompt to run choco.</summary>
    public bool ElevateWithUac { get; set; } = true;
}
