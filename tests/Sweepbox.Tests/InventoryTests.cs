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
}
