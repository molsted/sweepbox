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
        catch
        {
            Directory.Move(backup, target);
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
