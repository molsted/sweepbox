using System.Text.RegularExpressions;
using Sweepbox.Core.Running;
using Sweepbox.Core.Settings;

namespace Sweepbox.Core.Managers;

public sealed partial class BrewManager : PackageManager
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

    /// <summary>
    /// Formulae are their Cellar folder (exact). Casks are measured from their <c>brew info</c> entry: apps plus
    /// Caskroom (exact), or apps, uninstall paths and package receipts (an estimate).
    /// </summary>
    public override async Task<IReadOnlyDictionary<PackageKey, PackageSize?>> MeasureAsync(ICommandRunner runner, string executable,
        IReadOnlyList<InstalledPackage> packages, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = Unknown(packages).ToDictionary();
        var formulae = packages.Where(p => p.Kind == PackageKind.Formula).ToList();
        var casks = packages.Where(p => p.Kind == PackageKind.Cask).ToList();
        if (formulae.Count == 0 && casks.Count == 0)
            return result;
        var prefix = await QueryRootAsync(runner, executable, Env, cancellationToken, "--prefix");

        if (prefix is not null && formulae.Count > 0)
        {
            await Task.Run(() =>
            {
                foreach (var p in formulae)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var folder = Path.Combine(prefix, "Cellar", p.Name[(p.Name.LastIndexOf('/') + 1)..]);
                    if (Directory.Exists(folder))
                        result[PackageKey.Of(p)] = new PackageSize(DiskUsage.Measure([folder], cancellationToken), false);
                }
            }, cancellationToken);
        }
        if (casks.Count > 0)
            await MeasureCasksAsync(runner, executable, casks, prefix, result, cancellationToken);
        return result;
    }

    private static async Task MeasureCasksAsync(ICommandRunner runner, string executable, List<InstalledPackage> casks,
        string? prefix, Dictionary<PackageKey, PackageSize?> result, CancellationToken cancellationToken)
    {
        var byName = new Dictionary<string, CaskFootprint>(StringComparer.Ordinal);
        var footprints = await CaskInfoAsync(runner, executable, casks.Select(p => p.Name).ToList(), cancellationToken);
        foreach (var f in footprints)
            byName[f.Token] = f;
        foreach (var f in footprints)
            foreach (var alias in f.Aliases)
                byName.TryAdd(alias, f);

        var pkgutil = OperatingSystem.IsMacOS() ? runner.ResolveExecutable("pkgutil") : null;
        var receipts = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var p in casks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byName.TryGetValue(p.Name, out var cask))
                continue;
            try
            {
                // Caskroom folders are named as `brew list --cask` prints them.
                var caskroom = prefix is not null && BrewCaskSizes.IsSafeSegment(p.Name) ? Path.Combine(prefix, "Caskroom", p.Name) : null;
                var files = new List<string>();
                if (pkgutil is not null)
                {
                    foreach (var id in cask.PkgIds)
                    {
                        if (!receipts.TryGetValue(id, out var listed))
                            receipts[id] = listed = await ReceiptFilesAsync(runner, pkgutil, id, cancellationToken);
                        files.AddRange(listed);
                    }
                }
                result[PackageKey.Of(p)] = await Task.Run(() => BrewCaskSizes.Measure(cask, caskroom, files, cancellationToken), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Trace.WriteLine($"Couldn't measure cask {p.Name}: {ex}");
            }
        }
    }

    /// <summary>
    /// One <c>brew info</c> call for all casks. One stale cask fails the whole call, so on failure the casks brew names
    /// as unavailable are dropped and the call is retried once. If that fails too, or brew named none, each remaining
    /// cask is asked for alone (a few at a time) and those that still fail are left out.
    /// </summary>
    private static async Task<IReadOnlyList<CaskFootprint>> CaskInfoAsync(ICommandRunner runner, string executable,
        IReadOnlyList<string> tokens, CancellationToken cancellationToken)
    {
        var (all, lines) = await TryCaskInfoAsync(runner, executable, tokens, cancellationToken);
        if (all is not null)
            return all;

        var unavailable = ParseUnavailableCasks(lines).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remaining = tokens.Where(t => !unavailable.Contains(t)).ToList();
        if (remaining.Count < tokens.Count)
        {
            if (remaining.Count == 0)
                return [];
            (all, _) = await TryCaskInfoAsync(runner, executable, remaining, cancellationToken);
            if (all is not null)
                return all;
            if (remaining.Count == 1)
                return [];
        }
        else if (tokens.Count == 1)
        {
            return [];
        }

        using var gate = new SemaphoreSlim(PerTokenParallelism);
        var each = await Task.WhenAll(remaining.Select(async token =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return (await TryCaskInfoAsync(runner, executable, [token], cancellationToken)).Casks ?? [];
            }
            finally
            {
                gate.Release();
            }
        }));
        return [.. each.SelectMany(c => c)];
    }

    private const int PerTokenParallelism = 4;

    private static async Task<(IReadOnlyList<CaskFootprint>? Casks, IReadOnlyList<string> Lines)> TryCaskInfoAsync(
        ICommandRunner runner, string executable, IReadOnlyList<string> tokens, CancellationToken cancellationToken)
    {
        var output = await MeasureProbeAsync(runner, executable, Env, cancellationToken, ["info", "--json=v2", "--cask", .. tokens]);
        if (!output.Succeeded)
            return (null, output.Lines);
        // Warnings and errors are mixed into the output: keep the JSON object only.
        var text = string.Join('\n', output.Lines);
        int first = text.IndexOf('{'), last = text.LastIndexOf('}');
        if (first < 0 || last < first)
            return (null, output.Lines);
        try
        {
            return (BrewCaskSizes.ParseCaskInfo(text[first..(last + 1)], Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                output.Lines);
        }
        catch (System.Text.Json.JsonException ex)
        {
            System.Diagnostics.Trace.WriteLine($"brew info --cask {string.Join(' ', tokens)} printed unreadable JSON: {ex.Message}");
            return (null, output.Lines);
        }
    }

    [GeneratedRegex(@"Cask '(?<token>[^']+)' is unavailable|No available cask with the name ""(?<token>[^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnavailableCaskRegex();

    /// <summary>
    /// The tokens a failed <c>brew info --cask</c> names as unavailable, e.g.
    /// <c>Error: Cask 'stale' is unavailable: No Cask with this name exists.</c> (brew stops at the first one).
    /// </summary>
    internal static IReadOnlyList<string> ParseUnavailableCasks(IEnumerable<string> lines) =>
    [
        .. lines.SelectMany(l => UnavailableCaskRegex().Matches(l))
            .Select(m => m.Groups["token"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>The regular files a package receipt lists, or none when <c>pkgutil</c> knows no such receipt.</summary>
    private static async Task<IReadOnlyList<string>> ReceiptFilesAsync(ICommandRunner runner, string pkgutil, string id,
        CancellationToken cancellationToken)
    {
        var info = await MeasureProbeAsync(runner, pkgutil, NoEnv, cancellationToken, "--pkg-info", id);
        if (!info.Succeeded || BrewCaskSizes.ParsePkgRoot(info.Lines) is not { } root)
            return [];
        var files = await MeasureProbeAsync(runner, pkgutil, NoEnv, cancellationToken, "--files", id);
        if (!files.Succeeded)
            return [];
        return await Task.Run(() => BrewCaskSizes.ReceiptFiles(root, files.Lines).ToList(), cancellationToken);
    }

    private static readonly Dictionary<string, string> NoEnv = [];

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
