using PkgCleaner.Core.Managers;
using PkgCleaner.Core.Settings;

namespace PkgCleaner.Tests;

public class ManagerStepTests
{
    private static string[] Lines(PackageManager m, AppSettings s) =>
        m.BuildSteps(s, "/x/" + m.ExecutableName).Select(c => string.Join(' ', c.Arguments)).ToArray();

    [Fact]
    public void Brew_defaults_update_upgrade_autoremove_cleanup()
    {
        Assert.Equal(
            ["update", "upgrade --formula", "upgrade --cask", "autoremove", "cleanup"],
            Lines(new BrewManager(), new AppSettings()));
    }

    [Fact]
    public void Brew_greedy_and_prune_all_add_flags()
    {
        var s = new AppSettings();
        s.Brew.Greedy = true;
        s.Brew.PruneAll = true;
        s.Brew.Update = false;

        Assert.Equal(
            ["upgrade --formula", "upgrade --cask --greedy", "autoremove", "cleanup --prune=all"],
            Lines(new BrewManager(), s));
    }

    [Fact]
    public void Brew_disables_its_own_auto_update_between_steps()
    {
        var step = new BrewManager().BuildSteps(new AppSettings(), "brew")[0];
        Assert.Equal("1", step.Environment["HOMEBREW_NO_AUTO_UPDATE"]);
    }

    [Theory]
    [InlineData(NpmCacheMode.None, new[] { "update --global" })]
    [InlineData(NpmCacheMode.Verify, new[] { "update --global", "cache verify" })]
    [InlineData(NpmCacheMode.Clean, new[] { "update --global", "cache clean --force" })]
    public void Npm_cache_mode_controls_cache_step(NpmCacheMode mode, string[] expected)
    {
        var s = new AppSettings();
        s.Npm.CacheMode = mode;
        Assert.Equal(expected, Lines(new NpmManager(), s));
    }

    [Fact]
    public void Choco_except_list_is_trimmed_and_passed_through()
    {
        var s = new AppSettings();
        s.Choco.Except = " googlechrome ,  vscode,, ";

        var steps = new ChocoManager().BuildSteps(s, "choco");

        Assert.Contains("--except=googlechrome,vscode", steps[0].Arguments);
        Assert.Contains("--yes", steps[0].Arguments);
        Assert.Equal(["cache", "remove", "--yes"], steps[1].Arguments);
        Assert.All(steps, st => Assert.True(st.Elevate));
    }

    [Fact]
    public void Choco_without_except_has_no_except_flag()
    {
        var steps = new ChocoManager().BuildSteps(new AppSettings(), "choco");
        Assert.DoesNotContain(steps[0].Arguments, a => a.StartsWith("--except"));
    }

    [Fact]
    public void Command_line_quotes_arguments_with_spaces()
    {
        var spec = new PkgCleaner.Core.Running.CommandSpec("d", "/usr/bin/tool", ["a b", "c"]);
        Assert.Equal("tool \"a b\" c", spec.CommandLine);
    }
}
