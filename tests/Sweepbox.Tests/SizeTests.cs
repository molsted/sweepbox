using Sweepbox.Core.Managers;
using Sweepbox.Core.Running;

namespace Sweepbox.Tests;

public sealed class SizeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sweepbox-size").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    private string File(string relative, int bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void MeasurePaths_counts_files_and_directories_once()
    {
        var a = File("dir/a.bin", 100);
        File("dir/b.bin", 50);
        var loose = File("loose.bin", 30);
        var dir = Path.Combine(_root, "dir");

        Assert.Equal(180, DiskUsage.MeasurePaths([dir, dir + Path.DirectorySeparatorChar, a, loose], CancellationToken.None));
    }

    [Fact]
    public void MeasurePaths_does_not_treat_a_sibling_prefix_as_nested()
    {
        File("b/x.bin", 10);
        File("bc/y.bin", 20);
        Assert.Equal(30, DiskUsage.MeasurePaths([Path.Combine(_root, "b"), Path.Combine(_root, "bc")], CancellationToken.None));
    }

    [Fact]
    public void MeasurePaths_ignores_missing_paths() =>
        Assert.Equal(0, DiskUsage.MeasurePaths([Path.Combine(_root, "nope"), Path.Combine(_root, "nope.bin")], CancellationToken.None));

    [Fact]
    public void Measure_stops_when_cancelled()
    {
        for (var i = 0; i < 3000; i++)
            File($"many/f{i}.bin", 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => DiskUsage.Measure([Path.Combine(_root, "many")], cts.Token));
    }

    [Fact]
    public void PackageKey_ignores_companions() =>
        Assert.Equal(
            PackageKey.Of(new InstalledPackage("choco", "git", "1", PackageKind.Package, ["git.install"])),
            PackageKey.Of(new InstalledPackage("choco", "git", "2", PackageKind.Package)));
}
