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

    private static InstalledPackage Pkg(string manager, string name, PackageKind kind) => new(manager, name, null, kind);

    [Fact]
    public async Task Brew_formula_size_is_its_cellar_folder()
    {
        File("prefix/Cellar/wget/1.25.0/bin/wget", 1000);
        var runner = new FakeRunner();
        runner.ProbeAnswers["--prefix"] = Path.Combine(_root, "prefix");
        var wget = Pkg("brew", "molsted/tap/wget", PackageKind.Formula);
        var cask = Pkg("brew", "firefox", PackageKind.Cask);

        var sizes = await new BrewManager().MeasureAsync(runner, "/fake/brew", [wget, cask], CancellationToken.None);

        Assert.Equal(new PackageSize(1000, false), sizes[PackageKey.Of(wget)]);
        Assert.Null(sizes[PackageKey.Of(cask)]);
    }

    [Fact]
    public async Task Npm_size_is_its_global_folder_including_scoped()
    {
        File("root/@angular/cli/package.json", 300);
        File("root/typescript/index.js", 200);
        var runner = new FakeRunner();
        runner.ProbeAnswers["root -g"] = "npm warn something\n" + Path.Combine(_root, "root") + "\n";
        var cli = Pkg("npm", "@angular/cli", PackageKind.Package);
        var ts = Pkg("npm", "typescript", PackageKind.Package);

        var sizes = await new NpmManager().MeasureAsync(runner, "/fake/npm", [cli, ts], CancellationToken.None);

        Assert.Equal(new PackageSize(300, false), sizes[PackageKey.Of(cli)]);
        Assert.Equal(new PackageSize(200, false), sizes[PackageKey.Of(ts)]);
    }

    [Fact]
    public async Task Missing_folder_or_failed_probe_is_unknown()
    {
        Directory.CreateDirectory(Path.Combine(_root, "root"));
        var ts = Pkg("npm", "typescript", PackageKind.Package);
        var runner = new FakeRunner();
        runner.ProbeAnswers["root -g"] = Path.Combine(_root, "root");

        Assert.Null((await new NpmManager().MeasureAsync(runner, "/fake/npm", [ts], CancellationToken.None))[PackageKey.Of(ts)]);

        runner.ProbeExitCodes["root -g"] = 1;
        Assert.Null((await new NpmManager().MeasureAsync(runner, "/fake/npm", [ts], CancellationToken.None))[PackageKey.Of(ts)]);

        var wget = Pkg("brew", "wget", PackageKind.Formula);
        Assert.Null((await new BrewManager().MeasureAsync(new FakeRunner(), "/fake/brew", [wget], CancellationToken.None))[PackageKey.Of(wget)]);
    }

    [Fact]
    public async Task Cancelled_measuring_propagates()
    {
        File("root/typescript/index.js", 200);
        var runner = new FakeRunner();
        runner.ProbeAnswers["root -g"] = Path.Combine(_root, "root");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new NpmManager().MeasureAsync(runner, "/fake/npm", [Pkg("npm", "typescript", PackageKind.Package)], cts.Token));
    }

    [Fact]
    public async Task Default_manager_reports_unknown()
    {
        var choco = Pkg("choco", "git", PackageKind.Package);
        var sizes = await new ChocoManager().MeasureAsync(new FakeRunner(), "/fake/choco", [choco], CancellationToken.None);
        Assert.Single(sizes);
        Assert.Null(sizes[PackageKey.Of(choco)]);
    }

    // ---- Homebrew casks. Fixtures are trimmed from real `brew info --json=v2 --cask` output. ----

    private const string Home = "/Users/me";

    private static string Info(params string[] casks) =>
        $$"""
        {
          "formulae": [],
          "casks": [{{string.Join(",\n", casks)}}]
        }
        """;

    /// <summary>LibreOffice as printed by current Homebrew: the target sits beside the artifact kind.</summary>
    private static string AppCask(string token, string target, string extra = "") =>
        $$"""
        {
          "token": "{{token}}",
          "full_token": "{{token}}",
          "old_tokens": [],
          "tap": "homebrew/cask",
          "artifacts": [
            { "app": ["LibreOffice.app"], "target": "{{target}}" },
            { "uninstall_postflight": null },
            { "zap": [{ "trash": ["~/Library/Application Support/LibreOffice", "~/Library/Preferences/org.libreoffice.script.plist"] }] }{{extra}}
          ]
        }
        """;

    /// <summary>Zoom: a pkg installer with uninstall → pkgutil and delete (one of them a wildcard).</summary>
    private static string ZoomCask(string applications, string helper) =>
        $$"""
        {
          "token": "zoom",
          "full_token": "zoom",
          "old_tokens": [],
          "artifacts": [
            {
              "uninstall": [
                {
                  "launchctl": ["us.zoom.updater", "us.zoom.ZoomDaemon"],
                  "signal": ["KILL", "us.zoom.xos"],
                  "pkgutil": "us.zoom.pkg.videomeeting",
                  "delete": [
                    "{{applications}}/zoom.us.app",
                    "/Library/Logs/DiagnosticReports/zoom.us*",
                    "{{helper}}"
                  ]
                }
              ]
            },
            { "pkg": ["zoomusInstallerFull.pkg"] },
            { "postflight_steps": [{ "steps": [{ "name": "/Applications/zoom.us.app", "type": "terminate_process" }] }] },
            { "zap": [{ "delete": "/Library/Logs/zoomusinstall.log", "trash": ["~/.zoomus"] }] }
          ]
        }
        """;

    [Fact]
    public void Cask_app_uses_its_target_or_the_applications_folder()
    {
        var parsed = BrewCaskSizes.ParseCaskInfo(Info(
            AppCask("libreoffice", "/Applications/LibreOffice.app"),
            """
            { "token": "old-style", "artifacts": [{ "app": ["Foo.app", { "target": "~/Apps/Bar.app" }] }] }
            """,
            """
            { "token": "plain", "artifacts": [{ "app": ["Sub/Plain.app"] }, { "preflight": null }] }
            """), Home);

        Assert.Equal(["libreoffice", "old-style", "plain"], parsed.Select(c => c.Token));
        Assert.Equal(["/Applications/LibreOffice.app"], parsed[0].AppPaths);
        Assert.True(parsed[0].AppsOnly); // zap and uninstall_postflight don't count
        Assert.Empty(parsed[0].DeletePaths);
        Assert.Equal(["/Users/me/Apps/Bar.app"], parsed[1].AppPaths);
        Assert.Equal(["/Applications/Plain.app"], parsed[2].AppPaths);
        Assert.True(parsed[2].AppsOnly);
    }

    [Fact]
    public void Pkg_cask_has_delete_paths_and_receipts_but_is_not_apps_only()
    {
        var zoom = Assert.Single(BrewCaskSizes.ParseCaskInfo(Info(ZoomCask("/Applications", "/Library/PrivilegedHelperTools/us.zoom.ZoomDaemon")), Home));

        Assert.False(zoom.AppsOnly);
        Assert.Empty(zoom.AppPaths);
        Assert.Equal(["/Applications/zoom.us.app", "/Library/PrivilegedHelperTools/us.zoom.ZoomDaemon"], zoom.DeletePaths);
        Assert.Equal(["us.zoom.pkg.videomeeting"], zoom.PkgIds);
    }

    [Fact]
    public void Mixed_cask_expands_home_and_drops_regex_ids()
    {
        var mixed = Assert.Single(BrewCaskSizes.ParseCaskInfo(Info(
            """
            {
              "token": "mixed",
              "artifacts": [
                { "app": ["Mixed.app"] },
                { "uninstall": [{ "delete": "~/Library/Mixed", "pkgutil": ["com.mixed.core", "com.mixed.*"] },
                                { "delete": ["$HOME/Library/MixedToo", "/Library/Mixed[12]", "~", "/", "/Library/../etc"] }] },
                { "some_future_artifact": ["whatever"] },
                "not an object"
              ]
            }
            """), Home));

        Assert.False(mixed.AppsOnly);
        Assert.Equal(["/Applications/Mixed.app"], mixed.AppPaths);
        Assert.Equal(["/Users/me/Library/Mixed", "/Users/me/Library/MixedToo"], mixed.DeletePaths);
        Assert.Equal(["com.mixed.core"], mixed.PkgIds);
    }

    [Fact]
    public void Font_cask_and_unsafe_app_names_give_nothing_to_measure()
    {
        var parsed = BrewCaskSizes.ParseCaskInfo(Info(
            """
            { "token": "font-fira-code", "artifacts": [{ "font": ["FiraCode-Regular.ttf"] }] }
            """,
            """
            { "token": "sneaky", "artifacts": [{ "app": [".."] }, { "app": ["Ok.app", { "target": "../../etc" }] }] }
            """), Home);

        Assert.False(parsed[0].AppsOnly);
        Assert.Empty(parsed[0].AppPaths);
        Assert.Empty(parsed[0].DeletePaths);
        Assert.Empty(parsed[1].AppPaths);
    }

    [Fact]
    public void Cask_aliases_are_full_token_and_old_tokens()
    {
        var c = Assert.Single(BrewCaskSizes.ParseCaskInfo(Info(
            """
            { "token": "visual-studio-code", "full_token": "homebrew/cask/visual-studio-code", "old_tokens": ["vscode", "code"], "artifacts": [] }
            """), Home));

        Assert.Equal(["homebrew/cask/visual-studio-code", "vscode", "code"], c.Aliases);
        Assert.False(c.AppsOnly); // nothing at all is not "apps only"
    }

    [Theory]
    [InlineData("Applications", "/Applications")]
    [InlineData("", "/")]
    [InlineData("Library/Apple/", "/Library/Apple")]
    public void Pkg_root_joins_volume_and_location(string location, string expected) =>
        Assert.Equal(expected, BrewCaskSizes.ParsePkgRoot(
            ["package-id: com.example.pkg", "version: 1.0", "volume: /", $"location: {location} ", "install-time: 1754340092"]));

    [Fact]
    public void Pkg_root_is_null_without_a_volume_or_with_an_escaping_location()
    {
        Assert.Null(BrewCaskSizes.ParsePkgRoot(["No receipt for 'x' found at '/'."]));
        Assert.Null(BrewCaskSizes.ParsePkgRoot(["volume: /", "location: ../etc"]));
    }

    private string Apps => Path.Combine(_root, "Applications");
    private string Prefix => Path.Combine(_root, "prefix");

    private FakeRunner CaskRunner()
    {
        var runner = new FakeRunner();
        runner.ProbeAnswers["--prefix"] = Prefix;
        return runner;
    }

    private static Task<IReadOnlyDictionary<PackageKey, PackageSize?>> Measure(FakeRunner runner, params InstalledPackage[] packages) =>
        new BrewManager().MeasureAsync(runner, "/fake/brew", packages, CancellationToken.None);

    [Fact]
    public async Task Apps_only_cask_is_its_app_plus_caskroom_exactly()
    {
        File("Applications/LibreOffice.app/Contents/MacOS/soffice", 1000);
        File("prefix/Caskroom/libreoffice/26.8.3/LibreOffice.dmg.json", 10);
        var runner = CaskRunner();
        runner.ProbeAnswers["info --json=v2 --cask libreoffice"] =
            "Warning: Treating libreoffice as a cask.\n" + Info(AppCask("libreoffice", Apps + "/LibreOffice.app")) + "\nError: nothing really";
        var cask = Pkg("brew", "libreoffice", PackageKind.Cask);

        var sizes = await Measure(runner, cask);

        Assert.Equal(new PackageSize(1010, false), sizes[PackageKey.Of(cask)]);
    }

    [Fact]
    public async Task Missing_app_is_an_estimate_or_unknown()
    {
        File("Applications/One.app/Contents/x", 100);
        File("prefix/Caskroom/two/1.0/y", 7);
        File("prefix/Caskroom/gone/1.0/z", 5);
        var runner = CaskRunner();
        runner.ProbeAnswers["info --json=v2 --cask two gone"] = Info(
            $$"""
            { "token": "two", "artifacts": [{ "app": ["One.app"], "target": "{{Apps}}/One.app" }, { "app": ["Two.app"], "target": "{{Apps}}/Two.app" }] }
            """,
            $$"""
            { "token": "gone", "artifacts": [{ "app": ["Gone.app"], "target": "{{Apps}}/Gone.app" }] }
            """);
        var two = Pkg("brew", "two", PackageKind.Cask);
        var gone = Pkg("brew", "gone", PackageKind.Cask);

        var sizes = await Measure(runner, two, gone);

        Assert.Equal(new PackageSize(107, true), sizes[PackageKey.Of(two)]);
        Assert.Null(sizes[PackageKey.Of(gone)]); // Caskroom alone never makes a size
    }

    [Fact]
    public async Task Pkg_cask_is_an_estimate_from_deletes_and_receipt_files_counted_once()
    {
        if (!OperatingSystem.IsMacOS())
            return; // receipts are read with pkgutil, which only exists on macOS
        File("Applications/zoom.us.app/Contents/MacOS/zoom.us", 1000);
        var helper = File("Library/PrivilegedHelperTools/us.zoom.ZoomDaemon", 200);
        File("Library/Internet Plug-Ins/ZoomUsPlugIn.plugin/Contents/Info.plist", 30);
        File("Library/Internet Plug-Ins/unlisted.bin", 4000); // in a listed directory, but not a listed file
        File("prefix/Caskroom/zoom/6.4.0/zoomusInstallerFull.pkg", 5);
        File("escape.bin", 9000);
        var runner = CaskRunner();
        runner.Installed.Add("pkgutil");
        runner.ProbeAnswers["info --json=v2 --cask zoom"] = Info(ZoomCask(Apps, helper));
        runner.ProbeAnswers["--pkg-info us.zoom.pkg.videomeeting"] =
            $"package-id: us.zoom.pkg.videomeeting\nversion: 6.4.0\nvolume: {_root}\nlocation: \ninstall-time: 1754340092\n";
        runner.ProbeAnswers["--files us.zoom.pkg.videomeeting"] = string.Join('\n',
            "Applications/zoom.us.app",
            "Applications/zoom.us.app/Contents/MacOS/zoom.us", // inside a delete path: counted once
            "Library/Internet Plug-Ins",
            "Library/Internet Plug-Ins/ZoomUsPlugIn.plugin/Contents/Info.plist",
            "Library/Gone/missing.bin",
            "Library/../escape.bin");
        var zoom = Pkg("brew", "zoom", PackageKind.Cask);

        var sizes = await Measure(runner, zoom);

        Assert.Equal(new PackageSize(1000 + 200 + 30 + 5, true), sizes[PackageKey.Of(zoom)]);
        Assert.Contains(runner.Probes, p => p.Executable == "/fake/pkgutil");
    }

    [Fact]
    public async Task Failed_batch_falls_back_to_one_call_per_token()
    {
        File("Applications/LibreOffice.app/x", 50);
        var runner = CaskRunner();
        runner.ProbeAnswers["info --json=v2 --cask stale libre"] = "Error: Cask 'stale' is unavailable: No Cask with this name exists.";
        runner.ProbeExitCodes["info --json=v2 --cask stale libre"] = 1;
        runner.ProbeExitCodes["info --json=v2 --cask stale"] = 1;
        // The installed name is an old token of the current cask.
        runner.ProbeAnswers["info --json=v2 --cask libre"] = Info(AppCask("libreoffice", Apps + "/LibreOffice.app").Replace("\"old_tokens\": []", "\"old_tokens\": [\"libre\"]"));
        var stale = Pkg("brew", "stale", PackageKind.Cask);
        var libre = Pkg("brew", "libre", PackageKind.Cask);

        var sizes = await Measure(runner, stale, libre);

        Assert.Null(sizes[PackageKey.Of(stale)]);
        Assert.Equal(new PackageSize(50, false), sizes[PackageKey.Of(libre)]);
        Assert.Contains(runner.Probes, p => string.Join(' ', p.Arguments) == "info --json=v2 --cask stale");
    }

    [Fact]
    public async Task Cancelled_cask_measuring_propagates()
    {
        File("Applications/LibreOffice.app/x", 50);
        var runner = CaskRunner();
        runner.ProbeAnswers["info --json=v2 --cask libreoffice"] = Info(AppCask("libreoffice", Apps + "/LibreOffice.app"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new BrewManager().MeasureAsync(runner, "/fake/brew", [Pkg("brew", "libreoffice", PackageKind.Cask)], cts.Token));
    }
}
