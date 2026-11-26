using Sweepbox.Core.Running;
using Sweepbox.Core.Settings;

namespace Sweepbox.Core.Managers;

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

    public override async Task<ManagerInventory> ListInstalledAsync(ICommandRunner runner, string executable, CancellationToken cancellationToken)
    {
        string[] caskArgs = ["list", "--cask", "--versions"];
        string[] leafArgs = ["leaves", "--installed-on-request"];
        string[] formulaArgs = ["list", "--formula", "--versions"];
        var outputs = new List<ProbeOutput>();
        foreach (var args in new[] { caskArgs, leafArgs, formulaArgs })
        {
            var output = await ProbeAsync(runner, executable, Env, cancellationToken, args);
            if (output.ExitCode != 0)
                return ListFailed("brew " + string.Join(' ', args), output);
            outputs.Add(output);
        }
        return new ManagerInventory(Id, DisplayName,
            [.. ParseCasks(outputs[0].Lines), .. ParseFormulae(outputs[1].Lines, outputs[2].Lines)], null);
    }

    public override CommandSpec BuildUninstallStep(AppSettings settings, string executable, InstalledPackage package)
    {
        var env = new Dictionary<string, string>(Env);
        if (!settings.Brew.Autoremove) env["HOMEBREW_NO_AUTOREMOVE"] = "1";
        var kind = package.Kind == PackageKind.Cask ? "--cask" : "--formula";
        return new CommandSpec($"Uninstall {package.Name}", executable, ["uninstall", kind, package.Name]) { Environment = env };
    }

    /// <summary>Parses <c>brew list --cask --versions</c>: "name v1 v2", newest last.</summary>
    internal static IReadOnlyList<InstalledPackage> ParseCasks(IEnumerable<string> lines) =>
    [
        .. lines.Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length > 0)
            .Select(parts => new InstalledPackage("brew", parts[0], parts.Length > 1 ? parts[^1] : null, PackageKind.Cask)),
    ];

    /// <summary>Formulae installed on request. <c>leaves</c> may be tap-qualified; the version list uses short names.</summary>
    internal static IReadOnlyList<InstalledPackage> ParseFormulae(IEnumerable<string> leaves, IEnumerable<string> versionLines)
    {
        var versions = new Dictionary<string, string?>();
        foreach (var parts in versionLines.Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            if (parts.Length > 0)
                versions[parts[0]] = parts.Length > 1 ? parts[^1] : null;
        return
        [
            .. leaves.Select(l => l.Trim()).Where(l => l.Length > 0)
                .Select(name => new InstalledPackage("brew", name, versions.GetValueOrDefault(name[(name.LastIndexOf('/') + 1)..]), PackageKind.Formula)),
        ];
    }
}
