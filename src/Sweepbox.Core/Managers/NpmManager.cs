using System.Text.Json;
using Sweepbox.Core.Running;
using Sweepbox.Core.Settings;

namespace Sweepbox.Core.Managers;

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

    public override async Task<ManagerInventory> ListInstalledAsync(ICommandRunner runner, string executable, CancellationToken cancellationToken)
    {
        var output = await ProbeAsync(runner, executable, Env, cancellationToken, "ls", "--global", "--depth=0", "--json");
        // npm exits non-zero for harmless problems such as extraneous packages, so trust the JSON, not the exit code.
        // stdout and stderr share one stream, so npm warnings can surround the JSON: keep only the outermost braces.
        var text = string.Join('\n', output.Lines);
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
            text = text[start..(end + 1)];
        var packages = ParseGlobals(text);
        return packages is null
            ? ListFailed("npm ls --global --depth=0 --json", output)
            : new ManagerInventory(Id, DisplayName, packages, null);
    }

    /// <summary>Each package is its folder under the global root; scoped names are nested folders. Linked ones are unknown.</summary>
    public override async Task<IReadOnlyDictionary<PackageKey, PackageSize?>> MeasureAsync(ICommandRunner runner, string executable,
        IReadOnlyList<InstalledPackage> packages, CancellationToken cancellationToken)
    {
        var result = Unknown(packages).ToDictionary();
        if (packages.Count == 0 || await QueryRootAsync(runner, executable, Env, cancellationToken, "root", "-g") is not { } root)
            return result;

        return await Task.Run(() =>
        {
            foreach (var p in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var folder = Path.Combine([root, .. p.Name.Split('/')]);
                // A linked package (`npm link`) is someone's checkout elsewhere, not space this install takes.
                if (Directory.Exists(folder) && !File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint))
                    result[PackageKey.Of(p)] = new PackageSize(DiskUsage.Measure([folder], cancellationToken), false);
            }
            return (IReadOnlyDictionary<PackageKey, PackageSize?>)result;
        }, cancellationToken);
    }

    public override CommandSpec BuildUninstallStep(AppSettings settings, string executable, InstalledPackage package) =>
        new($"Uninstall {package.Name}", executable, ["uninstall", "--global", package.Name]) { Environment = Env };

    public override bool IsProtected(string packageName) => IsProtectedName(packageName);

    /// <summary>npm and corepack ship with Node.js and must not be uninstalled from here.</summary>
    private static bool IsProtectedName(string name) =>
        name.Equals("npm", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("corepack", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses <c>npm ls --global --depth=0 --json</c>. Returns null when the output isn't a JSON object,
    /// and an empty list when there is no "dependencies" object. Hides npm and corepack.
    /// </summary>
    internal static IReadOnlyList<InstalledPackage>? ParseGlobals(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            if (!doc.RootElement.TryGetProperty("dependencies", out var deps) || deps.ValueKind != JsonValueKind.Object)
                return [];

            return
            [
                .. deps.EnumerateObject()
                    .Where(dep => !IsProtectedName(dep.Name))
                    .Select(dep => new InstalledPackage("npm", dep.Name,
                        dep.Value.ValueKind == JsonValueKind.Object
                            && dep.Value.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                            ? v.GetString()
                            : null,
                        PackageKind.Package)),
            ];
        }
    }
}
