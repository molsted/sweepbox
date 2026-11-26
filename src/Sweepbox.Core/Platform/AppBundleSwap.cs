using System.Diagnostics;

namespace Sweepbox.Core.Platform;

/// <summary>
/// Replaces an installed app bundle with a staged copy using renames on the same volume, so the
/// app is never missing: if the swap fails half-way, the old bundle is put back.
/// </summary>
public static class AppBundleSwap
{
    /// <summary>A hidden sibling of <paramref name="target"/> to copy the new bundle to before swapping.</summary>
    public static string StagingPathFor(string target) =>
        Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileNameWithoutExtension(target)}-update-{Guid.NewGuid():N}.app");

    /// <summary>
    /// Removes hidden staged or old copies a previous, interrupted update left next to <paramref name="target"/>.
    /// </summary>
    public static void CleanUpLeftovers(string target)
    {
        var parent = Path.GetDirectoryName(target)!;
        var name = Path.GetFileNameWithoutExtension(target);
        foreach (var pattern in new[] { $".{name}-update-*.app", $".{name}-old-*.app" })
        foreach (var leftover in Directory.EnumerateDirectories(parent, pattern))
        {
            try { Directory.Delete(leftover, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"Update: could not remove leftover {leftover}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Moves <paramref name="target"/> aside, moves <paramref name="staged"/> into its place, then deletes the
    /// old copy. Throws if the swap fails, after restoring the old bundle.
    /// </summary>
    public static void Replace(string target, string staged)
    {
        var backup = Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileNameWithoutExtension(target)}-old-{Guid.NewGuid():N}.app");

        Directory.Move(target, backup);
        try
        {
            Directory.Move(staged, target);
        }
        catch (Exception swapError)
        {
            try
            {
                Directory.Move(backup, target);
            }
            catch (Exception rollbackError)
            {
                throw new IOException(
                    $"Could not put the new app in place ({swapError.Message}) nor restore the old one ({rollbackError.Message}); it is at {backup}.",
                    swapError);
            }
            throw;
        }

        // The running binary keeps executing from its open files after this.
        try
        {
            Directory.Delete(backup, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"Update: could not remove the previous app copy at {backup}: {ex.Message}");
        }
    }
}
