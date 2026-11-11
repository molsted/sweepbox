using PkgCleaner.Core.Running;
using PkgCleaner.Core.Settings;

namespace PkgCleaner.Core.Managers;

public abstract class PackageManager
{
    /// <summary>Short id, e.g. "brew".</summary>
    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    /// <summary>Name of the executable to look up on PATH.</summary>
    public abstract string ExecutableName { get; }

    public abstract bool IsSupportedOnThisOS { get; }

    public abstract bool IsEnabled(AppSettings settings);

    /// <summary>The commands for one maintenance run, in order.</summary>
    public abstract IReadOnlyList<CommandSpec> BuildSteps(AppSettings settings, string executable);

    public static IReadOnlyList<PackageManager> All { get; } = [new BrewManager(), new ChocoManager(), new NpmManager()];

    public static IReadOnlyList<PackageManager> ForThisOS => All.Where(m => m.IsSupportedOnThisOS).ToList();
}

public sealed class BrewManager : PackageManager
{
    private static readonly Dictionary<string, string> Env = new()
    {
        // We run `brew update` ourselves; don't let every later command do it again.
        ["HOMEBREW_NO_AUTO_UPDATE"] = "1",
        ["HOMEBREW_NO_ENV_HINTS"] = "1",
        ["HOMEBREW_NO_INSTALL_CLEANUP"] = "1",
        ["HOMEBREW_COLOR"] = "0",
        ["HOMEBREW_NO_COLOR"] = "1",
        ["NONINTERACTIVE"] = "1",
    };

    public override string Id => "brew";
    public override string DisplayName => "Homebrew";
    public override string ExecutableName => "brew";
    public override bool IsSupportedOnThisOS => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();
    public override bool IsEnabled(AppSettings settings) => settings.Brew.Enabled;

    public override IReadOnlyList<CommandSpec> BuildSteps(AppSettings settings, string executable)
    {
        var s = settings.Brew;
        var steps = new List<CommandSpec>();
        void Add(string description, params string[] args) =>
            steps.Add(new CommandSpec(description, executable, args) { Environment = Env });

        if (s.Update) Add("Update Homebrew", "update");
        if (s.UpgradeFormulae) Add("Upgrade formulae", "upgrade", "--formula");
        if (s.UpgradeCasks)
        {
            if (s.Greedy) Add("Upgrade casks (greedy)", "upgrade", "--cask", "--greedy");
            else Add("Upgrade casks", "upgrade", "--cask");
        }
        if (s.Autoremove) Add("Remove unused dependencies", "autoremove");
        if (s.Cleanup)
        {
            if (s.PruneAll) Add("Clean up (all cache)", "cleanup", "--prune=all");
            else Add("Clean up", "cleanup");
        }
        return steps;
    }
}

public sealed class NpmManager : PackageManager
{
    private static readonly Dictionary<string, string> Env = new()
    {
        ["npm_config_update_notifier"] = "false",
        ["npm_config_fund"] = "false",
        ["npm_config_audit"] = "false",
        ["npm_config_color"] = "false",
        ["npm_config_progress"] = "false",
    };

    public override string Id => "npm";
    public override string DisplayName => "npm (global)";
    public override string ExecutableName => "npm";
    public override bool IsSupportedOnThisOS => true;
    public override bool IsEnabled(AppSettings settings) => settings.Npm.Enabled;

    public override IReadOnlyList<CommandSpec> BuildSteps(AppSettings settings, string executable)
    {
        var s = settings.Npm;
        var steps = new List<CommandSpec>();
        void Add(string description, params string[] args) =>
            steps.Add(new CommandSpec(description, executable, args) { Environment = Env });

        if (s.UpdateGlobals) Add("Update global packages", "update", "--global");
        switch (s.CacheMode)
        {
            case NpmCacheMode.Verify: Add("Verify and garbage-collect cache", "cache", "verify"); break;
            case NpmCacheMode.Clean: Add("Clear cache", "cache", "clean", "--force"); break;
        }
        return steps;
    }
}

public sealed class ChocoManager : PackageManager
{
    public override string Id => "choco";
    public override string DisplayName => "Chocolatey";
    public override string ExecutableName => "choco";
    public override bool IsSupportedOnThisOS => OperatingSystem.IsWindows();
    public override bool IsEnabled(AppSettings settings) => settings.Choco.Enabled;

    public override IReadOnlyList<CommandSpec> BuildSteps(AppSettings settings, string executable)
    {
        var s = settings.Choco;
        var steps = new List<CommandSpec>();
        void Add(string description, params string[] args) =>
            steps.Add(new CommandSpec(description, executable, args) { Elevate = s.ElevateWithUac });

        if (s.UpgradeAll)
        {
            var args = new List<string> { "upgrade", "all", "--yes", "--no-progress", "--limit-output" };
            var except = string.Join(',', s.Except.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (except.Length > 0)
                args.Add($"--except={except}");
            Add("Upgrade all packages", [.. args]);
        }
        if (s.CacheRemove) Add("Remove download cache", "cache", "remove", "--yes");
        return steps;
    }
}
