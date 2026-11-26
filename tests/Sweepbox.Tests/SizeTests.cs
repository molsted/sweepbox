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
    public void Measure_stops_when_cancelled_mid_enumeration()
    {
        for (var i = 0; i < 3000; i++)
            File($"many/f{i}.bin", 1);
        using var cts = new CancellationTokenSource();
        var entries = 0;
        Assert.Throws<OperationCanceledException>(() => DiskUsage.Measure([Path.Combine(_root, "many")], cts.Token, () =>
        {
            if (++entries == 10)
                cts.Cancel();
        }));
        Assert.InRange(entries, 10, 1100);
    }

    [Fact]
    public void Measure_stops_when_entering_a_directory_after_cancel()
    {
        File("deep/a/b/c/x.bin", 1);
        using var cts = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => DiskUsage.Measure([Path.Combine(_root, "deep")], cts.Token, cts.Cancel));
    }

    [Fact]
    public void MeasurePaths_counts_a_nested_directory_once()
    {
        File("outer/inner/a.bin", 40);
        File("outer/b.bin", 2);
        Assert.Equal(42, DiskUsage.MeasurePaths([Path.Combine(_root, "outer", "inner"), Path.Combine(_root, "outer")], CancellationToken.None));
    }

    [Fact]
    public void Symlinks_inside_a_measured_directory_are_skipped()
    {
        File("real/a.bin", 10);
        File("target/big.bin", 1000);
        var link = Path.Combine(_root, "real", "dirlink");
        var fileLink = Path.Combine(_root, "real", "filelink.bin");
        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine(_root, "target"));
            System.IO.File.CreateSymbolicLink(fileLink, Path.Combine(_root, "target", "big.bin"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // symlink creation not permitted here (e.g. Windows without developer mode)
        }
        var real = Path.Combine(_root, "real");
        Assert.Equal(10, DiskUsage.Measure([real], CancellationToken.None));
        Assert.Equal(10, DiskUsage.MeasurePaths([real, fileLink], CancellationToken.None));
    }

    [Fact]
    public void IsInside_handles_roots_siblings_and_self()
    {
        var sep = Path.DirectorySeparatorChar;
        var root = Path.GetPathRoot(_root)!;
        Assert.False(DiskUsage.IsInside(root, root, StringComparison.Ordinal));
        Assert.True(DiskUsage.IsInside(_root, root, StringComparison.Ordinal));
        Assert.False(DiskUsage.IsInside(_root + "bc", _root, StringComparison.Ordinal));
        Assert.True(DiskUsage.IsInside(_root + sep + "bc", _root, StringComparison.Ordinal));
    }

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
