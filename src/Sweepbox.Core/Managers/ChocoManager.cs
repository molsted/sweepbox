using Sweepbox.Core.Platform;
using Sweepbox.Core.Running;
using Sweepbox.Core.Settings;

namespace Sweepbox.Core.Managers;

public sealed class ChocoManager : PackageManager
{
    private readonly IInstalledProgramsSource? _programs;

    /// <summary>Reads "Apps &amp; features" from the registry on Windows; elsewhere sizes stay unknown.</summary>
    public ChocoManager() : this(OperatingSystem.IsWindows() ? new RegistryInstalledPrograms() : null)
    {
    }

    public ChocoManager(IInstalledProgramsSource? programs) => _programs = programs;

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

    public override async Task<ManagerInventory> ListInstalledAsync(ICommandRunner runner, string executable, CancellationToken cancellationToken)
    {
        var env = new Dictionary<string, string>();
        // Chocolatey 1.x's `list` queries the remote feed unless told otherwise, so require 2.x.
        var version = await ProbeAsync(runner, executable, env, cancellationToken, "--version");
        if (!version.Succeeded)
            return ListFailed("choco --version", version);
        if (ParseMajorVersion(version.Lines) is not >= 2)
            return new ManagerInventory(Id, DisplayName, [], "Chocolatey 2 or later is needed to list packages");

        var list = await ProbeAsync(runner, executable, env, cancellationToken, "list", "--limit-output");
        return !list.Succeeded
            ? ListFailed("choco list --limit-output", list)
            : new ManagerInventory(Id, DisplayName, ParseList(list.Lines), null);
    }

    public override CommandSpec BuildUninstallStep(AppSettings settings, string executable, InstalledPackage package) =>
        new($"Uninstall {package.Name}", executable, ["uninstall", package.Name, "--yes", "--no-progress", "--limit-output"])
        {
            Elevate = settings.Choco.ElevateWithUac,
        };

    public override bool IsProtected(string packageName) => IsProtectedName(packageName);

    /// <summary>Chocolatey itself, and its own "chocolatey-*.extension" packages.</summary>
    private static bool IsProtectedName(string name) =>
        name.Equals("chocolatey", StringComparison.OrdinalIgnoreCase) ||
        (name.StartsWith("chocolatey-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".extension", StringComparison.OrdinalIgnoreCase));

    /// <summary>Major version from the first non-blank line, e.g. "2.4.1" gives 2; null if it isn't a version.</summary>
    internal static int? ParseMajorVersion(IEnumerable<string> lines)
    {
        var first = lines.Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (first is null) return null;
        var head = first.Split('.')[0];
        return int.TryParse(head, out var major) ? major : null;
    }

    /// <summary>
    /// Parses <c>choco list --limit-output</c> ("name|version"). Hides Chocolatey itself, and hides
    /// "X.install"/"X.portable" variants whose plain "X" package is also listed: they become X's companions
    /// (as listed), so uninstalling X removes them too.
    /// </summary>
    internal static IReadOnlyList<InstalledPackage> ParseList(IEnumerable<string> lines)
    {
        var all = lines.Select(l => l.Trim().Split('|'))
            .Where(parts => parts.Length == 2 && parts[0].Length > 0)
            .ToList();
        var names = new HashSet<string>(all.Select(p => p[0]), StringComparer.OrdinalIgnoreCase);
        var companions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var parts in all)
            if (BaseOfListedVariant(parts[0], names) is { } baseName)
                (companions.TryGetValue(baseName, out var list) ? list : companions[baseName] = []).Add(parts[0]);

        return
        [
            .. all.Where(parts => !IsProtectedName(parts[0]) && BaseOfListedVariant(parts[0], names) is null)
                .Select(parts => new InstalledPackage("choco", parts[0], parts[1].Length > 0 ? parts[1] : null, PackageKind.Package,
                    companions.GetValueOrDefault(parts[0]))),
        ];
    }

    /// <summary>"X" for an "X.install"/"X.portable" name whose plain "X" is also listed; otherwise null.</summary>
    private static string? BaseOfListedVariant(string name, HashSet<string> names)
    {
        foreach (var suffix in new[] { ".install", ".portable" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && names.Contains(name[..^suffix.Length]))
                return name[..^suffix.Length];
        return null;
    }

    /// <summary>
    /// Estimates each package from its "Apps &amp; features" entry (Windows' own EstimatedSize, so never exact).
    /// The registry is read once per call, off the calling thread.
    /// </summary>
    public override async Task<IReadOnlyDictionary<PackageKey, PackageSize?>> MeasureAsync(ICommandRunner runner, string executable,
        IReadOnlyList<InstalledPackage> packages, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = Unknown(packages).ToDictionary();
        if (_programs is null || packages.Count == 0)
            return result;

        var programs = await Task.Run(() => _programs.Read(), cancellationToken);
        foreach (var p in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MatchEstimatedSizeKb(p.Name, programs) is { } kb)
                result[PackageKey.Of(p)] = new PackageSize(kb * 1024, true);
        }
        return result;
    }

    /// <summary>
    /// The size in KiB of the one "Apps &amp; features" entry that belongs to a Chocolatey package, or null when none or
    /// several could. Names are compared as lowercase letters and digits only. Entries that are system components,
    /// child entries (updates) or have no size are ignored, and entries with the same name (HKLM and HKCU, 32- and
    /// 64-bit) collapse into one, keeping the largest size. An exact name match wins; otherwise a name that starts with
    /// the id counts if the original name has a boundary right after it ("7-Zip 24.09" for 7zip, not "GitHub" for git),
    /// and only when exactly one such entry exists.
    /// </summary>
    internal static long? MatchEstimatedSizeKb(string packageId, IReadOnlyList<InstalledProgram> programs)
    {
        var id = packageId;
        foreach (var suffix in new[] { ".install", ".portable" })
            if (id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                id = id[..^suffix.Length];
                break;
            }
        id = Normalize(id);
        if (id.Length == 0)
            return null;

        var candidates = new Dictionary<string, (string Name, long Kb)>();
        foreach (var p in programs)
        {
            if (p.SystemComponent || !string.IsNullOrEmpty(p.ParentKeyName) || p.EstimatedSizeKb is not > 0 || string.IsNullOrEmpty(p.DisplayName))
                continue;
            var key = Normalize(p.DisplayName);
            if (key.Length == 0)
                continue;
            if (!candidates.TryGetValue(key, out var existing) || p.EstimatedSizeKb.Value > existing.Kb)
                candidates[key] = (p.DisplayName, p.EstimatedSizeKb.Value);
        }

        if (candidates.TryGetValue(id, out var exact))
            return exact.Kb;

        var matches = candidates.Where(c => c.Key.StartsWith(id, StringComparison.Ordinal) && BoundaryAfter(c.Value.Name, id.Length)).ToList();
        return matches.Count == 1 ? matches[0].Value.Kb : null;
    }

    private static string Normalize(string s) =>
        string.Concat(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant));

    /// <summary>True when the original name continues with a boundary after its first <paramref name="letters"/> letters/digits.</summary>
    private static bool BoundaryAfter(string name, int letters)
    {
        var seen = 0;
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsLetterOrDigit(name[i]) && ++seen == letters)
                return i + 1 < name.Length && (char.IsDigit(name[i + 1]) || name[i + 1] is ' ' or '(' or '-' or '_' or '.');
        }
        return false;
    }
}
