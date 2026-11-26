using System.Text.Json;
using Sweepbox.Core.Running;

namespace Sweepbox.Core.Managers;

/// <summary>
/// What a cask put on disk, as far as its <c>brew info --json=v2</c> entry tells. <paramref name="Aliases"/> holds the
/// <c>full_token</c> and <c>old_tokens</c>, so an installed name can be matched to the current cask.
/// </summary>
internal sealed record CaskFootprint(string Token, IReadOnlyList<string> AppPaths, bool AppsOnly,
    IReadOnlyList<string> DeletePaths, IReadOnlyList<string> PkgIds, IReadOnlyList<string> Aliases);

/// <summary>Parsing and measuring for Homebrew cask sizes. Probing lives in <see cref="BrewManager"/>.</summary>
internal static class BrewCaskSizes
{
    private const string Applications = "/Applications";

    /// <summary>Package ids are literal names; brew would treat anything with these characters as a regex.</summary>
    private static readonly char[] RegexChars = ['*', '+', '?', '[', ']', '(', ')', '{', '}', '|', '^', '$', '\\'];

    private static readonly char[] Wildcards = ['*', '?', '['];

    /// <summary>
    /// Reads the <c>casks</c> array. Unknown artifact kinds are tolerated; <c>zap</c> (user data) and the
    /// pre/postflight hooks are ignored. Throws <see cref="JsonException"/> when the text isn't JSON.
    /// </summary>
    internal static IReadOnlyList<CaskFootprint> ParseCaskInfo(string json, string home)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<CaskFootprint>();
        if (!doc.RootElement.TryGetProperty("casks", out var casks) || casks.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var cask in casks.EnumerateArray())
        {
            if (cask.ValueKind == JsonValueKind.Object && String(cask, "token") is { Length: > 0 } token)
                result.Add(ParseCask(cask, token, home));
        }
        return result;
    }

    private static CaskFootprint ParseCask(JsonElement cask, string token, string home)
    {
        var apps = new List<string>();
        var deletes = new List<string>();
        var pkgIds = new List<string>();
        var sawApp = false;
        var onlyApps = true;

        if (cask.TryGetProperty("artifacts", out var artifacts) && artifacts.ValueKind == JsonValueKind.Array)
        {
            foreach (var artifact in artifacts.EnumerateArray())
            {
                if (artifact.ValueKind != JsonValueKind.Object)
                    continue;
                foreach (var property in artifact.EnumerateObject())
                {
                    switch (property.Name)
                    {
                        case "target" or "zap":
                            break; // an option of the artifact beside it; user data cleanup
                        case var name when name.Contains("flight", StringComparison.Ordinal):
                            break; // preflight/postflight hooks and their *_steps
                        case "binary" or "command_wrapper" or "manpage" or "bash_completion" or "zsh_completion" or "fish_completion":
                            break; // symlinks or tiny wrappers into the app or Caskroom, not footprint of their own
                        case "app":
                            sawApp = true;
                            if (AppPath(property.Value, artifact, home) is { } app)
                                apps.Add(app);
                            break;
                        case "uninstall":
                            onlyApps = false;
                            foreach (var directive in ObjectsOf(property.Value))
                            {
                                foreach (var path in Strings(directive, "delete"))
                                    if (ExpandPath(path, home) is { } full && full.IndexOfAny(Wildcards) < 0)
                                        deletes.Add(full);
                                foreach (var id in Strings(directive, "pkgutil"))
                                    if (id.Length > 0 && id.IndexOfAny(RegexChars) < 0)
                                        pkgIds.Add(id);
                            }
                            break;
                        default:
                            onlyApps = false; // pkg, installer, binary, font, suite, …
                            break;
                    }
                }
            }
        }

        var aliases = new List<string>();
        if (String(cask, "full_token") is { Length: > 0 } fullToken)
            aliases.Add(fullToken);
        aliases.AddRange(Strings(cask, "old_tokens").Where(t => t.Length > 0));

        return new CaskFootprint(token, apps.Distinct().ToList(), sawApp && onlyApps, deletes.Distinct().ToList(),
            pkgIds.Distinct().ToList(), aliases);
    }

    /// <summary>
    /// The app's installed path: the target (beside the kind in current Homebrew, or an options object inside the
    /// array in older output), else /Applications plus the source's file name.
    /// </summary>
    private static string? AppPath(JsonElement value, JsonElement artifact, string home)
    {
        string? source = null, target = String(artifact, "target");
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    source ??= item.GetString();
                else if (item.ValueKind == JsonValueKind.Object)
                    target ??= String(item, "target");
            }
        }

        if (!string.IsNullOrEmpty(target))
        {
            if (target.StartsWith('~') || target.StartsWith("$HOME", StringComparison.Ordinal) || target.StartsWith('/'))
                return ExpandPath(target, home);
            return IsSafeRelative(target) ? Applications + "/" + target : null;
        }
        if (source is null)
            return null;
        var fileName = source.TrimEnd('/')[(source.TrimEnd('/').LastIndexOf('/') + 1)..];
        return IsSafeSegment(fileName) ? Applications + "/" + fileName : null;
    }

    /// <summary>
    /// Expands <c>~</c> and <c>$HOME</c>. Returns null for paths that aren't absolute afterwards, that contain
    /// "." or ".." segments, or that are the root or the home folder itself.
    /// </summary>
    private static string? ExpandPath(string path, string home)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
            path = home + path[1..];
        else if (path == "$HOME" || path.StartsWith("$HOME/", StringComparison.Ordinal))
            path = home + path[5..];
        if (!path.StartsWith('/'))
            return null;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
            return null;
        var normalized = "/" + string.Join('/', segments);
        return normalized == home.TrimEnd('/') ? null : normalized;
    }

    /// <summary>One folder or file name that stays inside its parent.</summary>
    internal static bool IsSafeSegment(string name) =>
        name.Length > 0 && name is not ("." or "..") && name.IndexOfAny(['/', '\\']) < 0;

    /// <summary>A relative path whose every segment is a safe name.</summary>
    private static bool IsSafeRelative(string path) =>
        !path.StartsWith('/') && path.Split('/', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } segments && segments.All(IsSafeSegment);

    /// <summary>
    /// The folder a receipt's files are relative to: <c>volume</c> joined with <c>location</c> (empty means the
    /// volume root), from <c>pkgutil --pkg-info</c>. Null without a volume or when the location escapes it.
    /// </summary>
    internal static string? ParsePkgRoot(IEnumerable<string> pkgInfoLines)
    {
        string? volume = null, location = null;
        foreach (var line in pkgInfoLines)
        {
            if (line.StartsWith("volume:", StringComparison.Ordinal))
                volume = line["volume:".Length..].Trim();
            else if (line.StartsWith("location:", StringComparison.Ordinal))
                location = line["location:".Length..].Trim();
        }
        if (string.IsNullOrEmpty(volume) || !volume.StartsWith('/'))
            return null;
        if (string.IsNullOrEmpty(location) || location.Trim('/').Length == 0)
            return volume.Length > 1 ? volume.TrimEnd('/') : volume;
        if (!IsSafeRelative(location.Trim('/')))
            return null;
        return volume.TrimEnd('/') + "/" + location.Trim('/');
    }

    /// <summary>
    /// The regular files among a receipt's listed paths (relative to <paramref name="root"/>). Directories are
    /// skipped because their files are listed separately; symlinks, missing files and escaping paths are skipped too.
    /// </summary>
    internal static IEnumerable<string> ReceiptFiles(string root, IEnumerable<string> relativePaths)
    {
        foreach (var line in relativePaths)
        {
            var relative = line.TrimEnd('\r');
            if (!IsSafeRelative(relative))
                continue;
            var path = Path.Combine(root, relative);
            if (IsRegularFile(path))
                yield return path;
        }
    }

    private static bool IsRegularFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return !attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // missing (FileNotFoundException/DirectoryNotFoundException are IOExceptions) or unreadable
        }
    }

    /// <summary>
    /// Exact when the cask is only apps and every app exists: the apps plus Caskroom. Otherwise an estimate from the
    /// existing apps, existing delete paths and receipt files, plus Caskroom; null when none of those exists.
    /// </summary>
    internal static PackageSize? Measure(CaskFootprint cask, string? caskroom, IEnumerable<string> receiptFiles,
        CancellationToken cancellationToken)
    {
        var apps = cask.AppPaths.Where(Exists).ToList();
        var extra = caskroom is null ? Array.Empty<string>() : [caskroom];
        if (cask.AppsOnly && apps.Count == cask.AppPaths.Count && apps.Count > 0)
            return new PackageSize(DiskUsage.MeasurePaths([.. apps, .. extra], cancellationToken), false);

        List<string> found = [.. apps, .. cask.DeletePaths.Where(Exists), .. receiptFiles];
        if (found.Count == 0)
            return null;
        return new PackageSize(DiskUsage.MeasurePaths([.. found, .. extra], cancellationToken), true);
    }

    private static bool Exists(string path) => Directory.Exists(path) || File.Exists(path);

    private static string? String(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A property that may be a string or an array of strings.</summary>
    private static IEnumerable<string> Strings(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value))
            yield break;
        if (value.ValueKind == JsonValueKind.String)
            yield return value.GetString()!;
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String)
                    yield return item.GetString()!;
    }

    /// <summary>An object, or the objects of an array.</summary>
    private static IEnumerable<JsonElement> ObjectsOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => [value],
        JsonValueKind.Array => value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList(),
        _ => [],
    };
}
