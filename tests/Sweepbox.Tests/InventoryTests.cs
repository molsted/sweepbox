using Sweepbox.Core.Managers;
using Sweepbox.Core.Settings;

namespace Sweepbox.Tests;

public sealed class InventoryTests
{
    [Fact]
    public void Brew_casks_take_the_newest_version_and_skip_blank_lines()
    {
        var casks = BrewManager.ParseCasks(["google-chrome 131.0.6778.86", "zoom 6.2.10 6.2.11", ""]);
        Assert.Equal([("google-chrome", "131.0.6778.86"), ("zoom", "6.2.11")], casks.Select(p => (p.Name, p.Version)));
        Assert.All(casks, p => { Assert.Equal("brew", p.ManagerId); Assert.Equal(PackageKind.Cask, p.Kind); });
    }

    [Fact]
    public void Brew_formulae_are_only_leaves_with_versions_joined_by_short_name()
    {
        var f = BrewManager.ParseFormulae(
            ["ffmpeg", "wget", "molsted/tap/sweeptool"],
            ["ffmpeg 7.1", "wget 1.25.0", "sweeptool 0.3", "libx265 4.0"]);
        Assert.Equal([("ffmpeg", "7.1"), ("molsted/tap/sweeptool", "0.3"), ("wget", "1.25.0")],
            f.Select(p => (p.Name, p.Version)).OrderBy(x => x.Name));
        Assert.All(f, p => Assert.Equal(PackageKind.Formula, p.Kind));
    }

    [Fact]
    public async Task Brew_lists_casks_and_formulae_through_probes()
    {
        var runner = new FakeRunner();
        runner.ProbeAnswers["list --cask --versions"] = "zoom 6.2.11";
        runner.ProbeAnswers["leaves --installed-on-request"] = "wget";
        runner.ProbeAnswers["list --formula --versions"] = "wget 1.25.0\nlibx265 4.0";
        var inv = await new BrewManager().ListInstalledAsync(runner, "/fake/brew", CancellationToken.None);
        Assert.Null(inv.Error);
        Assert.Equal(["zoom", "wget"], inv.Packages.Select(p => p.Name));
        Assert.All(runner.Probes, p => Assert.Equal("1", p.Environment["HOMEBREW_NO_AUTO_UPDATE"]));
    }

    [Fact]
    public async Task Brew_listing_failure_becomes_an_error_not_an_exception()
    {
        var runner = new FakeRunner();
        runner.ProbeExitCodes["list --cask --versions"] = 1;
        var inv = await new BrewManager().ListInstalledAsync(runner, "/fake/brew", CancellationToken.None);
        Assert.Empty(inv.Packages);
        Assert.Equal("Couldn't list Homebrew packages: \"brew list --cask --versions\" failed (exit 1).", inv.Error);
    }

    [Theory]
    [InlineData(PackageKind.Cask, "zoom", new[] { "uninstall", "--cask", "zoom" })]
    [InlineData(PackageKind.Formula, "molsted/tap/sweeptool", new[] { "uninstall", "--formula", "molsted/tap/sweeptool" })]
    public void Brew_uninstall_arguments(PackageKind kind, string name, string[] expected)
    {
        var step = new BrewManager().BuildUninstallStep(new AppSettings(), "/x/brew", new("brew", name, null, kind));
        Assert.Equal(expected, step.Arguments);
        Assert.Equal($"Uninstall {name}", step.Description);
        Assert.False(step.IsCleanup);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Brew_uninstall_follows_the_autoremove_setting(bool autoremove, bool expectNoAutoremove)
    {
        var s = new AppSettings();
        s.Brew.Autoremove = autoremove;
        var step = new BrewManager().BuildUninstallStep(s, "/x/brew", new("brew", "wget", null, PackageKind.Formula));
        Assert.Equal(expectNoAutoremove, step.Environment.TryGetValue("HOMEBREW_NO_AUTOREMOVE", out var v) && v == "1");
        Assert.Equal("1", step.Environment["HOMEBREW_NO_AUTO_UPDATE"]);
    }

    [Fact]
    public void Choco_list_hides_protected_and_dependency_variants()
    {
        var p = ChocoManager.ParseList([
            "chocolatey|2.4.1", "chocolatey-core.extension|1.4.0", "git|2.47.1", "git.install|2.47.1",
            "7zip.portable|24.9.0", "vcredist140|14.42.34433", "not a package line", ""]);
        Assert.Equal(["7zip.portable", "git", "vcredist140"], p.Select(x => x.Name).OrderBy(n => n));
        Assert.Equal("2.47.1", p.Single(x => x.Name == "git").Version);
        Assert.All(p, x => { Assert.Equal("choco", x.ManagerId); Assert.Equal(PackageKind.Package, x.Kind); });
    }

    [Theory]
    [InlineData("2.4.1", 2)]
    [InlineData("1.4.0", 1)]
    [InlineData("garbage", null)]
    public void Choco_major_version(string line, int? expected) => Assert.Equal(expected, ChocoManager.ParseMajorVersion([line]));

    [Fact]
    public async Task Choco_1_shows_a_clear_error_instead_of_the_remote_feed()
    {
        var runner = new FakeRunner();
        runner.ProbeAnswers["--version"] = "1.4.0";
        var inv = await new ChocoManager().ListInstalledAsync(runner, "/fake/choco", CancellationToken.None);
        Assert.Empty(inv.Packages);
        Assert.Equal("Chocolatey 2 or later is needed to list packages", inv.Error);
        Assert.DoesNotContain(runner.Probes, p => p.Arguments.Contains("list"));
    }

    [Fact]
    public async Task Choco_2_lists_packages_through_a_probe()
    {
        var runner = new FakeRunner();
        runner.ProbeAnswers["--version"] = "2.4.1";
        runner.ProbeAnswers["list --limit-output"] = "chocolatey|2.4.1\ngit|2.47.1";
        var inv = await new ChocoManager().ListInstalledAsync(runner, "/fake/choco", CancellationToken.None);
        Assert.Null(inv.Error);
        Assert.Equal(["git"], inv.Packages.Select(p => p.Name));
    }

    [Fact]
    public async Task Choco_listing_failure_becomes_an_error_not_an_exception()
    {
        var runner = new FakeRunner();
        runner.ProbeAnswers["--version"] = "2.4.1";
        runner.ProbeExitCodes["list --limit-output"] = 1;
        var inv = await new ChocoManager().ListInstalledAsync(runner, "/fake/choco", CancellationToken.None);
        Assert.Empty(inv.Packages);
        Assert.Equal("Couldn't list Chocolatey packages: \"choco list --limit-output\" failed (exit 1).", inv.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Choco_uninstall_arguments_and_elevation(bool elevate)
    {
        var s = new AppSettings();
        s.Choco.ElevateWithUac = elevate;
        var step = new ChocoManager().BuildUninstallStep(s, "choco", new("choco", "git", "2.47.1", PackageKind.Package));
        Assert.Equal(["uninstall", "git", "--yes", "--no-progress", "--limit-output"], step.Arguments);
        Assert.Equal(elevate, step.Elevate);
    }

    [Theory]
    [InlineData("chocolatey", true)]
    [InlineData("Chocolatey-Windowsupdate.Extension", true)]
    [InlineData("git", false)]
    [InlineData("chocolateygui", false)]
    public void Choco_protected_names(string name, bool expected) => Assert.Equal(expected, new ChocoManager().IsProtected(name));

    [Fact]
    public void Choco_list_keeps_lookalikes_and_variants_without_a_base_package()
    {
        var p = ChocoManager.ParseList(["chocolateygui|2.1.1", "foo.install|1.0", "bar|1.0", "Bar.Portable|1.0"]);
        Assert.Equal(["chocolateygui", "foo.install", "bar"], p.Select(x => x.Name));
    }
}
