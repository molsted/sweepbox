using System.Text.RegularExpressions;
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

    /// <summary>
    /// Casks, then formulae installed on request. If the casks list but the formulae don't, the casks are
    /// returned with the error (a partial inventory).
    /// </summary>
    public override async Task<ManagerInventory> ListInstalledAsync(ICommandRunner runner, string executable, CancellationToken cancellationToken)
    {
        var casks = await ProbeAsync(runner, executable, Env, cancellationToken, "list", "--cask", "--versions");
        if (!casks.Succeeded)
            return ListFailed("brew list --cask --versions", casks);
        var caskPackages = ParseCasks(casks.Lines);

        var leaves = await ProbeAsync(runner, executable, Env, cancellationToken, "leaves", "--installed-on-request");
        if (!leaves.Succeeded)
            return ListFailed("brew leaves --installed-on-request", leaves, caskPackages);
        var versions = await ProbeAsync(runner, executable, Env, cancellationToken, "list", "--formula", "--versions");
        if (!versions.Succeeded)
            return ListFailed("brew list --formula --versions", versions, caskPackages);

        return new ManagerInventory(Id, DisplayName, [.. caskPackages, .. ParseFormulae(leaves.Lines, versions.Lines)], null);
    }

    /// <summary>Formulae are their Cellar folder (exact). Casks are not measured yet.</summary>
    public override async Task<IReadOnlyDictionary<PackageKey, PackageSize?>> MeasureAsync(ICommandRunner runner, string executable,
        IReadOnlyList<InstalledPackage> packages, CancellationToken cancellationToken)
    {
        var result = Unknown(packages).ToDictionary();
        var formulae = packages.Where(p => p.Kind == PackageKind.Formula).ToList();
        if (formulae.Count == 0 || await QueryRootAsync(runner, executable, Env, cancellationToken, "--prefix") is not { } prefix)
            return result;

        return await Task.Run(() =>
        {
            foreach (var p in formulae)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var folder = Path.Combine(prefix, "Cellar", p.Name[(p.Name.LastIndexOf('/') + 1)..]);
                if (Directory.Exists(folder))
                    result[PackageKey.Of(p)] = new PackageSize(DiskUsage.Measure([folder], cancellationToken), false);
            }
            return (IReadOnlyDictionary<PackageKey, PackageSize?>)result;
        }, cancellationToken);
    }

    public override CommandSpec BuildUninstallStep(AppSettings settings, string executable, InstalledPackage package)
    {
        var env = new Dictionary<string, string>(Env);
        if (!settings.Brew.Autoremove) env["HOMEBREW_NO_AUTOREMOVE"] = "1";
        var kind = package.Kind == PackageKind.Cask ? "--cask" : "--formula";
        return new CommandSpec($"Uninstall {package.Name}", executable, ["uninstall", kind, package.Name]) { Environment = env };
    }

    /// <summary>
    /// A package name, optionally tap-qualified ("user/tap/name"). Homebrew merges warnings and errors into
    /// the output, and lines such as "Warning: …" must not become packages.
    /// </summary>
    private static readonly Regex PackageName = new(@"^[A-Za-z0-9@._+-]+(/[A-Za-z0-9@._+-]+){0,2}$", RegexOptions.CultureInvariant);

    /// <summary>Splits each line into words and keeps lines whose first word is a package name.</summary>
    private static IEnumerable<string[]> PackageLines(IEnumerable<string> lines) =>
        lines.Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length > 0 && PackageName.IsMatch(parts[0]));

    /// <summary>Parses <c>brew list --cask --versions</c>: "name v1 v2", newest last.</summary>
    internal static IReadOnlyList<InstalledPackage> ParseCasks(IEnumerable<string> lines) =>
    [
        .. PackageLines(lines)
            .Select(parts => new InstalledPackage("brew", parts[0], parts.Length > 1 ? parts[^1] : null, PackageKind.Cask)),
    ];

    /// <summary>Formulae installed on request. <c>leaves</c> may be tap-qualified; the version list uses short names.</summary>
    internal static IReadOnlyList<InstalledPackage> ParseFormulae(IEnumerable<string> leaves, IEnumerable<string> versionLines)
    {
        var versions = new Dictionary<string, string?>();
        foreach (var parts in PackageLines(versionLines))
            versions[parts[0]] = parts.Length > 1 ? parts[^1] : null;
        return
        [
            .. PackageLines(leaves).Select(parts => parts[0])
                .Select(name => new InstalledPackage("brew", name, versions.GetValueOrDefault(name[(name.LastIndexOf('/') + 1)..]), PackageKind.Formula)),
        ];
    }
}
