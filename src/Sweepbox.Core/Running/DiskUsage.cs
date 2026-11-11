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

    /// <summary>Total size in bytes of all files under the given directories (missing ones count as 0).</summary>
    public static long Measure(IEnumerable<string> directories)
    {
        long total = 0;
        foreach (var dir in directories.Distinct())
        {
            if (!Directory.Exists(dir))
                continue;
            try
            {
                foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", Options))
                {
                    try { total += file.Length; }
                    catch (IOException) { } // deleted while enumerating
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return total;
    }

    public static Task<long> MeasureAsync(IEnumerable<string> directories, CancellationToken cancellationToken = default) =>
        Task.Run(() => Measure(directories), cancellationToken);

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
