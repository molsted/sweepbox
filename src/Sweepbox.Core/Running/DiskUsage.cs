namespace Sweepbox.Core.Running;

public static class DiskUsage
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        // Don't follow symlinks/junctions: avoids loops and counting files twice.
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    private const int CancellationInterval = 1000;

    /// <summary>Total size in bytes of all files under the given directories (missing ones count as 0).</summary>
    public static long Measure(IEnumerable<string> directories) => Measure(directories, CancellationToken.None);

    /// <summary>As <see cref="Measure(IEnumerable{string})"/>, throwing <see cref="OperationCanceledException"/> when cancelled.</summary>
    public static long Measure(IEnumerable<string> directories, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long total = 0;
        var seen = 0;
        foreach (var dir in directories.Distinct())
            total += MeasureDirectory(dir, cancellationToken, ref seen);
        return total;
    }

    /// <summary>
    /// Total size of files and/or directories. Paths are normalized and deduplicated, and any path equal to or inside
    /// another listed directory is dropped so nothing is counted twice. Missing paths and symlinks are ignored.
    /// </summary>
    public static long MeasurePaths(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        var normalized = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Normalize)
            .Distinct(comparer)
            .ToList();
        var directories = normalized.Where(Directory.Exists).ToList();
        var prefixes = directories.Select(d => d.EndsWith(Path.DirectorySeparatorChar) ? d : d + Path.DirectorySeparatorChar).ToList();

        long total = 0;
        var seen = 0;
        foreach (var path in normalized)
        {
            if (prefixes.Any(prefix => path.StartsWith(prefix, comparison)))
                continue; // inside another listed directory (a directory never matches its own prefix)

            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(path))
            {
                if (!IsReparsePoint(path))
                    total += MeasureDirectory(path, cancellationToken, ref seen);
            }
            else if (File.Exists(path) && !IsReparsePoint(path))
            {
                try { total += new FileInfo(path).Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return total;
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return full.Length > (root?.Length ?? 0) ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    private static bool IsReparsePoint(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private static long MeasureDirectory(string dir, CancellationToken cancellationToken, ref int seen)
    {
        if (!Directory.Exists(dir))
            return 0;
        long total = 0;
        try
        {
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", Options))
            {
                if (++seen % CancellationInterval == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                try { total += file.Length; }
                catch (IOException) { } // deleted while enumerating
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return total;
    }

    public static Task<long> MeasureAsync(IEnumerable<string> directories, CancellationToken cancellationToken = default) =>
        Task.Run(() => Measure(directories, cancellationToken), cancellationToken);

    /// <summary>Human-readable size: decimal units on macOS (like Finder), binary on Windows (like Explorer).</summary>
    public static string Format(long bytes)
    {
        double unit = OperatingSystem.IsWindows() ? 1024 : 1000;
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var i = 0;
        while (value >= unit && i < suffixes.Length - 1)
        {
            value /= unit;
            i++;
        }
        var number = value.ToString(i == 0 ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture);
        return $"{number} {suffixes[i]}";
    }
}
