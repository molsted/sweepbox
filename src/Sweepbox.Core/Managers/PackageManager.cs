using Sweepbox.Core.Running;
using Sweepbox.Core.Settings;

namespace Sweepbox.Core.Managers;

public abstract class PackageManager
{
    /// <summary>Short id, e.g. "brew".</summary>
    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    /// <summary>Name used in running text, e.g. "npm" rather than "npm (global)".</summary>
    public virtual string ShortName => DisplayName;

    /// <summary>Name of the executable to look up on PATH.</summary>
    public abstract string ExecutableName { get; }

    public abstract bool IsSupportedOnThisOS { get; }

    public abstract bool IsEnabled(AppSettings settings);

    /// <summary>The commands for one maintenance run, in order. Steps that free disk space have IsCleanup set.</summary>
    public abstract IReadOnlyList<CommandSpec> BuildSteps(AppSettings settings, string executable);

    /// <summary>
    /// Directories that cleanup steps shrink. Their total size is measured before the first and after the
    /// last cleanup step to report how much space was freed.
    /// </summary>
    public virtual Task<IReadOnlyList<string>> GetCleanupLocationsAsync(ICommandRunner runner, string executable, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>Runs a quick query command (e.g. <c>brew --cache</c>) and returns its last line if it is an absolute path.</summary>
    protected static async Task<string?> QueryPathAsync(ICommandRunner runner, string executable,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken, params string[] args)
    {
        var lines = new List<string>();
        var spec = new CommandSpec("Locate " + string.Join(' ', args), executable, args) { Environment = environment, IsProbe = true };
        var result = await runner.RunAsync(spec, lines.Add, TimeSpan.FromSeconds(30), cancellationToken);
        var last = lines.Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        return result.Success && last is not null && Path.IsPathRooted(last) ? last : null;
    }

    public static IReadOnlyList<PackageManager> All { get; } = [new BrewManager(), new ChocoManager(), new NpmManager()];

    public static IReadOnlyList<PackageManager> ForThisOS => All.Where(m => m.IsSupportedOnThisOS).ToList();

    /// <summary>"Homebrew and npm" / "Chocolatey and npm" - for UI text that should match the platform.</summary>
    public static string JoinNames(IEnumerable<PackageManager> managers)
    {
        var names = managers.Select(m => m.ShortName).ToList();
        return names.Count switch
        {
            0 => "your package managers",
            1 => names[0],
            _ => string.Join(", ", names[..^1]) + " and " + names[^1],
        };
    }
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
        void Clean(string description, params string[] args) =>
            steps.Add(new CommandSpec(description, executable, args) { Environment = Env, IsCleanup = true });

        if (s.Update) Add("Update Homebrew", "update");
        if (s.UpgradeFormulae) Add("Upgrade formulae", "upgrade", "--formula");
        if (s.UpgradeCasks)
        {
            if (s.Greedy) Add("Upgrade casks (greedy)", "upgrade", "--cask", "--greedy");
            else Add("Upgrade casks", "upgrade", "--cask");
        }
        if (s.Autoremove) Clean("Remove unused dependencies", "autoremove");
        if (s.Cleanup)
        {
            if (s.PruneAll) Clean("Clean up (all cache)", "cleanup", "--prune=all");
            else Clean("Clean up", "cleanup");
        }
        return steps;
    }

    /// <summary>Download cache plus installed kegs/casks (cleanup removes old versions there).</summary>
    public override async Task<IReadOnlyList<string>> GetCleanupLocationsAsync(ICommandRunner runner, string executable, CancellationToken cancellationToken)
    {
        var locations = new List<string>();
        if (await QueryPathAsync(runner, executable, Env, cancellationToken, "--cache") is { } cache)
            locations.Add(cache);
        if (await QueryPathAsync(runner, executable, Env, cancellationToken, "--prefix") is { } prefix)
        {
            locations.Add(Path.Combine(prefix, "Cellar"));
            locations.Add(Path.Combine(prefix, "Caskroom"));
        }
        return locations;
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
    public override string ShortName => "npm";
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
            case NpmCacheMode.Verify: Clean("Verify and garbage-collect cache", "cache", "verify"); break;
            case NpmCacheMode.Clean: Clean("Clear cache", "cache", "clean", "--force"); break;
        }
        return steps;

        void Clean(string description, params string[] args) =>
            steps.Add(new CommandSpec(description, executable, args) { Environment = Env, IsCleanup = true });
    }

    public override async Task<IReadOnlyList<string>> GetCleanupLocationsAsync(ICommandRunner runner, string executable, CancellationToken cancellationToken) =>
        await QueryPathAsync(runner, executable, Env, cancellationToken, "config", "get", "cache") is { } cache ? [cache] : [];
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
        if (s.CacheRemove)
            steps.Add(new CommandSpec("Remove download cache", executable, ["cache", "remove", "--yes"]) { Elevate = s.ElevateWithUac, IsCleanup = true });
        return steps;
    }

    /// <summary>Chocolatey caches downloads in the user's and the system's TEMP folder.</summary>
    public override Task<IReadOnlyList<string>> GetCleanupLocationsAsync(ICommandRunner runner, string executable, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(
        [
            Path.Combine(Path.GetTempPath(), "chocolatey"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", "chocolatey"),
        ]);
}
