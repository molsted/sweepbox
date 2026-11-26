using Sweepbox.Core.Managers;
using Sweepbox.Core.Running;
using Sweepbox.Core.Settings;

namespace Sweepbox.Tests;

public sealed class InventoryTests
{
    private readonly AppSettings _settings = new();

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
    public void Brew_parsing_ignores_warnings_and_errors_mixed_into_the_output()
    {
        var casks = BrewManager.ParseCasks([
            "Warning: Unexpected method 'appcast' called on Cask foo.", "zoom 6.2.11",
            "Error: Cask 'bar' is unreadable: wrong number of arguments", "google-chrome 131.0"]);
        Assert.Equal(["zoom", "google-chrome"], casks.Select(p => p.Name));

        var formulae = BrewManager.ParseFormulae(
            ["Warning: some leaves warning", "wget", "molsted/tap/sweeptool", "Error: something broke"],
            ["Warning: Unexpected method 'appcast' called on Cask foo.", "wget 1.25.0", "sweeptool 0.3", "Error: x 1"]);
        Assert.Equal([("wget", "1.25.0"), ("molsted/tap/sweeptool", "0.3")], formulae.Select(p => (p.Name, p.Version)));
    }

    [Fact]
    public async Task Brew_keeps_casks_when_the_formula_listing_fails()
    {
        var runner = new FakeRunner();
        runner.ProbeAnswers["list --cask --versions"] = "zoom 6.2.11";
        runner.ProbeExitCodes["list --formula --versions"] = 1;
        var inv = await new BrewManager().ListInstalledAsync(runner, "/fake/brew", CancellationToken.None);
        Assert.Equal(["zoom"], inv.Packages.Select(p => p.Name));
        Assert.Equal("Couldn't list Homebrew packages: \"brew list --formula --versions\" failed (exit 1).", inv.Error);
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
        Assert.Equal(["git.install"], p.Single(x => x.Name == "git").Companions!);
        Assert.Null(p.Single(x => x.Name == "7zip.portable").Companions);
        Assert.All(p, x => { Assert.Equal("choco", x.ManagerId); Assert.Equal(PackageKind.Package, x.Kind); });
    }

    [Fact]
    public void Choco_list_attaches_hidden_variants_as_companions_with_their_listed_casing()
    {
        var p = ChocoManager.ParseList(["Bar.Portable|1.0", "bar|1.0", "foo.install|1.0", "baz|1", "baz.install|1", "baz.portable|1"]);
        Assert.Equal(["bar", "foo.install", "baz"], p.Select(x => x.Name));
        Assert.Equal(["Bar.Portable"], p[0].Companions!);
        Assert.Null(p[1].Companions);
        Assert.Equal(["baz.install", "baz.portable"], p[2].Companions!);
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

    [Fact]
    public void Npm_globals_parse_and_skip_protected()
    {
        var p = NpmManager.ParseGlobals("""{"dependencies":{"typescript":{"version":"5.6.3"},"npm":{"version":"10.9.0"},"corepack":{"version":"0.30.0"},"@angular/cli":{"version":"19.0.1"}}}""");
        Assert.Equal([("@angular/cli", "19.0.1"), ("typescript", "5.6.3")], p!.Select(x => (x.Name, x.Version)).OrderBy(x => x.Name));
        Assert.All(p!, x => { Assert.Equal("npm", x.ManagerId); Assert.Equal(PackageKind.Package, x.Kind); });
    }

    [Fact]
    public void Npm_json_without_dependencies_is_empty_and_junk_is_null()
    {
        Assert.Empty(NpmManager.ParseGlobals("""{"name":"lib"}""")!);
        Assert.Null(NpmManager.ParseGlobals("npm ERR! something"));
    }

    [Fact]
    public async Task Npm_lists_even_when_ls_exits_non_zero_with_valid_json()
    {
        var runner = new FakeRunner();
        runner.ProbeAnswers["ls --global --depth=0 --json"] = """{"dependencies":{"typescript":{"version":"5.6.3"}},"problems":["extraneous"]}""";
        runner.ProbeExitCodes["ls --global --depth=0 --json"] = 1;
        var inv = await new NpmManager().ListInstalledAsync(runner, "/fake/npm", CancellationToken.None);
        Assert.Null(inv.Error);
        Assert.Equal(["typescript"], inv.Packages.Select(p => p.Name));
    }

    [Fact]
    public async Task Npm_listing_failure_without_json_becomes_an_error_not_an_exception()
    {
        var runner = new FakeRunner();
        runner.ProbeAnswers["ls --global --depth=0 --json"] = "npm ERR! something";
        runner.ProbeExitCodes["ls --global --depth=0 --json"] = 1;
        var inv = await new NpmManager().ListInstalledAsync(runner, "/fake/npm", CancellationToken.None);
        Assert.Empty(inv.Packages);
        Assert.Equal("Couldn't list npm packages: \"npm ls --global --depth=0 --json\" failed (exit 1): npm ERR! something.", inv.Error);
    }

    [Fact]
    public async Task Listing_failure_names_the_last_output_line()
    {
        var runner = new FakeRunner();
        runner.ProbeAnswers["--version"] = "2.4.1";
        runner.ProbeAnswers["list --limit-output"] = "Chocolatey v2.4.1\nError: access denied\n\n";
        runner.ProbeExitCodes["list --limit-output"] = 2;
        var inv = await new ChocoManager().ListInstalledAsync(runner, "/fake/choco", CancellationToken.None);
        Assert.Equal("Couldn't list Chocolatey packages: \"choco list --limit-output\" failed (exit 2): Error: access denied.", inv.Error);
    }

    [Fact]
    public async Task Listing_timeout_says_so()
    {
        var runner = new FakeRunner();
        runner.ProbeAnswers["list --cask --versions"] = "zoom 6.2.11";
        runner.ProbeTimeouts.Add("list --cask --versions");
        var inv = await new BrewManager().ListInstalledAsync(runner, "/fake/brew", CancellationToken.None);
        Assert.Empty(inv.Packages);
        Assert.Equal("Couldn't list Homebrew packages: \"brew list --cask --versions\" failed (timed out after 60 s).", inv.Error);
    }

    [Fact]
    public async Task Npm_lists_when_warnings_surround_the_json()
    {
        var runner = new FakeRunner();
        runner.ProbeAnswers["ls --global --depth=0 --json"] = "npm warn config global `--global` is deprecated\n{\"dependencies\":{\"typescript\":{\"version\":\"5.6.3\"}}}\nnpm notice done";
        var inv = await new NpmManager().ListInstalledAsync(runner, "/fake/npm", CancellationToken.None);
        Assert.Null(inv.Error);
        Assert.Equal(["typescript"], inv.Packages.Select(p => p.Name));
    }

    [Fact]
    public void Npm_dependency_without_version_has_null_version()
    {
        var p = NpmManager.ParseGlobals("{\"dependencies\":{\"left-pad\":{}}}");
        var single = Assert.Single(p!);
        Assert.Equal("left-pad", single.Name);
        Assert.Null(single.Version);
    }

    [Fact]
    public void Npm_uninstall_arguments()
    {
        var step = new NpmManager().BuildUninstallStep(new AppSettings(), "npm", new("npm", "@angular/cli", null, PackageKind.Package));
        Assert.Equal(["uninstall", "--global", "@angular/cli"], step.Arguments);
        Assert.Equal("false", step.Environment["npm_config_fund"]);
        Assert.Equal("Uninstall @angular/cli", step.Description);
    }

    [Theory]
    [InlineData("npm", true)]
    [InlineData("NPM", true)]
    [InlineData("corepack", true)]
    [InlineData("typescript", false)]
    [InlineData("npm-check", false)]
    public void Npm_protected_names(string name, bool expected) => Assert.Equal(expected, new NpmManager().IsProtected(name));

    private const string NpmJson = """{"dependencies":{"typescript":{"version":"5.6.3"}}}""";

    private PackageInventory InventoryFor(FakeRunner runner, params PackageManager[] managers) =>
        new(() => _settings, _ => Task.FromResult<ICommandRunner>(runner), managers);

    [Fact]
    public async Task Inventory_lists_disabled_managers_and_skips_missing_ones()
    {
        _settings.Npm.Enabled = false;
        var runner = new FakeRunner();
        runner.Installed.Remove("brew");
        runner.ProbeAnswers["ls --global --depth=0 --json"] = NpmJson;
        var result = await InventoryFor(runner, new BrewManager(), new NpmManager()).ListAsync(CancellationToken.None);
        var only = Assert.Single(result);
        Assert.Equal("npm", only.ManagerId);
        Assert.Equal(["typescript"], only.Packages.Select(p => p.Name));
    }

    [Fact]
    public async Task Inventory_keeps_other_managers_when_one_throws()
    {
        var runner = new FakeRunner();
        runner.ProbeAnswers["ls --global --depth=0 --json"] = NpmJson;
        var result = await InventoryFor(runner, new ThrowingManager(), new NpmManager()).ListAsync(CancellationToken.None);
        Assert.Equal(2, result.Count);
        Assert.Equal("boom", result[0].Error);
        Assert.Empty(result[0].Packages);
        Assert.Null(result[1].Error);
        Assert.Equal(["typescript"], result[1].Packages.Select(p => p.Name));
    }

    [Fact]
    public async Task Inventory_propagates_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InventoryFor(new FakeRunner(), new CancelingManager()).ListAsync(cts.Token));
    }

    [Fact]
    public void Notes_follow_settings_and_elevation()
    {
        const string brewNote = "Homebrew will also remove dependencies that nothing else uses.";
        const string chocoNote = "You'll be asked for administrator permission once per Chocolatey package.";
        InstalledPackage[] both = [new("brew", "wget", null, PackageKind.Formula), new("choco", "git", null, PackageKind.Package)];
        InstalledPackage[] npmOnly = [new("npm", "typescript", null, PackageKind.Package)];

        Assert.Equal([brewNote, chocoNote], UninstallNotes.For(_settings, both, isElevated: false));
        Assert.Equal([brewNote], UninstallNotes.For(_settings, both, isElevated: true));
        Assert.Empty(UninstallNotes.For(_settings, npmOnly, isElevated: false));

        _settings.Brew.Autoremove = false;
        _settings.Choco.ElevateWithUac = false;
        Assert.Empty(UninstallNotes.For(_settings, both, isElevated: false));
    }

    private static InstalledPackage Pkg(string manager, string name) => new(manager, name, null, PackageKind.Package);

    private static ManagerInventory Inv(string manager, params string[] names) =>
        new(manager, manager, names.Select(n => Pkg(manager, n)).ToList(), null);

    [Fact]
    public async Task Measure_reports_each_manager_separately_in_order()
    {
        var runner = new FakeRunner();
        var reports = new List<IReadOnlyDictionary<PackageKey, PackageSize?>>();
        await InventoryFor(runner, new SizedManager("a", 5), new SizedManager("b", 7)).MeasureAsync(
            [Inv("a", "x"), Inv("b", "y", "z")], reports.Add, CancellationToken.None);
        Assert.Equal(2, reports.Count);
        Assert.Equal(5, reports[0][new("a", "x", PackageKind.Package)]!.Bytes);
        Assert.Equal(2, reports[1].Count);
        Assert.Equal(7, reports[1][new("b", "z", PackageKind.Package)]!.Bytes);
    }

    [Fact]
    public async Task Measure_isolates_a_failing_manager()
    {
        var runner = new FakeRunner();
        var reports = new List<IReadOnlyDictionary<PackageKey, PackageSize?>>();
        await InventoryFor(runner, new ThrowingManager(), new SizedManager("a", 5)).MeasureAsync(
            [Inv("boom", "x"), Inv("a", "y")], reports.Add, CancellationToken.None);
        Assert.Equal(2, reports.Count);
        Assert.Null(Assert.Single(reports[0]).Value);
        Assert.Equal(5, Assert.Single(reports[1]).Value!.Bytes);
    }

    [Fact]
    public async Task Measure_propagates_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var reports = new List<IReadOnlyDictionary<PackageKey, PackageSize?>>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InventoryFor(new FakeRunner(), new CancelingManager()).MeasureAsync([Inv("boom", "x")], reports.Add, cts.Token));
        Assert.Empty(reports);
    }

    [Fact]
    public async Task Measure_skips_empty_and_unmatched_inventories_and_nulls_unresolvable_ones()
    {
        var runner = new FakeRunner();
        runner.Installed.Remove("brew");
        var reports = new List<IReadOnlyDictionary<PackageKey, PackageSize?>>();
        var emptyErrored = new ManagerInventory("a", "a", [], "broken");
        await InventoryFor(runner, new SizedManager("a", 5), new ThrowingManager()).MeasureAsync(
            [Inv("a"), emptyErrored, Inv("nobody", "q"), Inv("boom", "gone")], reports.Add, CancellationToken.None);
        var only = Assert.Single(reports);
        Assert.Null(Assert.Single(only).Value);
    }

    [Fact]
    public async Task Measure_measures_an_errored_inventory_that_still_has_packages()
    {
        var reports = new List<IReadOnlyDictionary<PackageKey, PackageSize?>>();
        var partial = new ManagerInventory("a", "a", [Pkg("a", "x")], "formula listing failed");
        await InventoryFor(new FakeRunner(), new SizedManager("a", 5)).MeasureAsync([partial], reports.Add, CancellationToken.None);
        Assert.Equal(5, Assert.Single(Assert.Single(reports)).Value!.Bytes);
    }

    [Fact]
    public async Task Measure_fills_keys_a_manager_left_out_with_null()
    {
        var reports = new List<IReadOnlyDictionary<PackageKey, PackageSize?>>();
        await InventoryFor(new FakeRunner(), new SizedManager("a", 5, skipLast: true)).MeasureAsync(
            [Inv("a", "x", "y")], reports.Add, CancellationToken.None);
        var r = Assert.Single(reports);
        Assert.Equal(5, r[new("a", "x", PackageKind.Package)]!.Bytes);
        Assert.True(r.ContainsKey(new("a", "y", PackageKind.Package)));
        Assert.Null(r[new("a", "y", PackageKind.Package)]);
    }

    [Fact]
    public async Task Measure_survives_duplicate_packages_when_a_manager_throws_or_cannot_run()
    {
        var runner = new FakeRunner();
        var reports = new List<IReadOnlyDictionary<PackageKey, PackageSize?>>();
        await InventoryFor(runner, new ThrowingManager(), new SizedManager("a", 5)).MeasureAsync(
            [Inv("boom", "x", "x"), Inv("a", "y")], reports.Add, CancellationToken.None);
        Assert.Equal(2, reports.Count);
        Assert.Null(Assert.Single(reports[0]).Value);
        Assert.Equal(5, Assert.Single(reports[1]).Value!.Bytes);

        runner.Installed.Remove("brew");
        reports.Clear();
        await InventoryFor(runner, new ThrowingManager(), new SizedManager("a", 5)).MeasureAsync(
            [Inv("boom", "x", "x"), Inv("a", "y")], reports.Add, CancellationToken.None);
        Assert.Equal(2, reports.Count);
        Assert.Null(Assert.Single(reports[0]).Value);
    }

    [Fact]
    public async Task Measure_treats_cancellation_without_a_cancelled_token_as_a_failure()
    {
        var reports = new List<IReadOnlyDictionary<PackageKey, PackageSize?>>();
        await InventoryFor(new FakeRunner(), new CancelingManager(), new SizedManager("a", 5)).MeasureAsync(
            [Inv("boom", "x"), Inv("a", "y")], reports.Add, CancellationToken.None);
        Assert.Equal(2, reports.Count);
        Assert.Null(Assert.Single(reports[0]).Value);
        Assert.Equal(5, Assert.Single(reports[1]).Value!.Bytes);
    }

    private sealed class SizedManager(string id, long bytes, bool skipLast = false) : ThrowingManager
    {
        public override string Id => id;
        public override Task<IReadOnlyDictionary<PackageKey, PackageSize?>> MeasureAsync(ICommandRunner runner, string executable,
            IReadOnlyList<InstalledPackage> packages, CancellationToken cancellationToken)
        {
            var take = skipLast ? packages.Take(packages.Count - 1) : packages;
            return Task.FromResult<IReadOnlyDictionary<PackageKey, PackageSize?>>(
                take.ToDictionary(PackageKey.Of, _ => (PackageSize?)new PackageSize(bytes, false)));
        }
    }

    private class ThrowingManager : PackageManager
    {
        public override string Id => "boom";
        public override string DisplayName => "Boom";
        public override string ExecutableName => "brew";
        public override bool IsSupportedOnThisOS => true;
        public override bool IsEnabled(AppSettings settings) => true;
        public override IReadOnlyList<CommandSpec> BuildSteps(AppSettings settings, string executable) => [];
        public override Task<ManagerInventory> ListInstalledAsync(ICommandRunner runner, string executable, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
        public override Task<IReadOnlyDictionary<PackageKey, PackageSize?>> MeasureAsync(ICommandRunner runner, string executable,
            IReadOnlyList<InstalledPackage> packages, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class CancelingManager : ThrowingManager
    {
        public override Task<ManagerInventory> ListInstalledAsync(ICommandRunner runner, string executable, CancellationToken cancellationToken) =>
            throw new OperationCanceledException(cancellationToken);
        public override Task<IReadOnlyDictionary<PackageKey, PackageSize?>> MeasureAsync(ICommandRunner runner, string executable,
            IReadOnlyList<InstalledPackage> packages, CancellationToken cancellationToken) =>
            throw new OperationCanceledException(cancellationToken);
    }
}
