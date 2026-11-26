using Sweepbox.Core.Platform;

namespace Sweepbox.Tests;

public sealed class AppBundleSwapTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sweepbox-swap-").FullName;

    private string Bundle(string name, string marker)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.Combine(path, "Contents"));
        File.WriteAllText(Path.Combine(path, "Contents", "marker.txt"), marker);
        return path;
    }

    private static string Marker(string bundle) => File.ReadAllText(Path.Combine(bundle, "Contents", "marker.txt"));

    [Fact]
    public void Replace_puts_the_staged_bundle_in_place_and_removes_the_old_one()
    {
        var target = Bundle("Sweepbox.app", "old");
        var staged = Bundle(".Sweepbox-staged.app", "new");

        AppBundleSwap.Replace(target, staged);

        Assert.Equal("new", Marker(target));
        Assert.False(Directory.Exists(staged));
        Assert.Equal(["Sweepbox.app"], Directory.GetDirectories(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void A_failed_swap_leaves_the_old_bundle_in_place()
    {
        var target = Bundle("Sweepbox.app", "old");
        var missing = Path.Combine(_dir, ".Sweepbox-staged.app"); // staging never happened

        Assert.ThrowsAny<IOException>(() => AppBundleSwap.Replace(target, missing));

        Assert.Equal("old", Marker(target));
        Assert.Equal(["Sweepbox.app"], Directory.GetDirectories(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Staging_path_is_a_hidden_sibling_on_the_same_volume()
    {
        var staged = AppBundleSwap.StagingPathFor(Path.Combine(_dir, "Sweepbox.app"));

        Assert.Equal(_dir, Path.GetDirectoryName(staged));
        Assert.StartsWith(".Sweepbox-update-", Path.GetFileName(staged));
        Assert.EndsWith(".app", staged);
    }

    [Fact]
    public void Leftovers_from_an_interrupted_update_are_removed()
    {
        var target = Bundle("Sweepbox.app", "current");
        Bundle(".Sweepbox-update-abc.app", "staged");
        Bundle(".Sweepbox-old-def.app", "old");
        Bundle("Other.app", "keep");

        AppBundleSwap.CleanUpLeftovers(target);

        Assert.Equal(["Other.app", "Sweepbox.app"], Directory.GetDirectories(_dir).Select(Path.GetFileName).Order());
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
