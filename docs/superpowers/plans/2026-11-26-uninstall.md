# Installed Tab and Uninstall: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an Installed tab to the Status window. It lists what Homebrew, Chocolatey and npm have installed, and uninstalls the packages the user ticks, as a logged run through the existing engine.

**Architecture:**
- Each `PackageManager` gains a listing method, built from probe commands and pure parsers, and an uninstall-step builder.
- A Core `PackageInventory` collects the listings.
- `MaintenanceEngine.UninstallAsync` reuses the run loop, with uninstall steps instead of maintenance steps.
- Uninstall runs are kept out of scheduling and the Overview through `RunHistoryStore.LatestMaintenance`.
- Desktop adds `InstalledViewModel`, a tab and a confirmation dialog.

**Tech Stack:** .NET 10, C# latest, Avalonia 12, CommunityToolkit.Mvvm, xUnit 2, `FakeTimeProvider`.

**Spec:** `docs/superpowers/specs/2026-11-26-uninstall-design.md`. Read it with this plan.

## Global Constraints

- **Core:** `TreatWarningsAsErrors`, nullable on, file-scoped namespaces matching folders, `sealed` classes.
- **Testability:** Core starts processes only through `ICommandRunner` and gets time only from `TimeProvider`. Parsers are `internal static` and pure, and tests reach them through `InternalsVisibleTo`.
- **No input:** commands never wait for input. Never pass `--force`, `--zap`, `--ignore-dependencies` or `--force-dependencies`.
- **Protected (never listed, never uninstalled):** npm `npm`, `corepack`. Chocolatey `chocolatey` and `chocolatey-*.extension`. Case-insensitive.
- **Chocolatey dependency variants:** `X.install` / `X.portable` are hidden when `X` is also installed.
- **Chocolatey version:** 2.x only. On 1.x the group error reads exactly `Chocolatey 2 or later is needed to list packages`.
- **Homebrew:** only casks and formulae from `brew leaves --installed-on-request` are listed. The uninstall step sets `HOMEBREW_NO_AUTOREMOVE=1` when `settings.Brew.Autoremove` is false.
- **Listing probes:** `IsProbe = true`, 60 s timeout, the manager's own environment variables.
- **Uninstall runs** use `RunTrigger.Uninstall`, have no cleanup measurement (`BytesFreed` stays null), and never change `EngineStatus.LastRun` or `Scheduler.NextRunAt`.
- **UI text, exact:**
  - group titles: `Homebrew – apps (casks)`, `Homebrew – command-line tools`, `Chocolatey`, `npm (global)`
  - button: `Uninstall selected ({n})`
  - busy message: `Sweepbox is busy – the list will refresh when it finishes`
  - notifications: `Uninstall failed` / `Uninstall finished`
  - Homebrew note in the dialog: `Homebrew will also remove dependencies that nothing else uses.`
  - Chocolatey note in the dialog: `You'll be asked for administrator permission once per Chocolatey package.`

## Review Focus

1. **`npm ls` exits non-zero but prints valid JSON** (peer-dependency or "extraneous" problems are common). The list must still show the packages. Test in Task 3.
2. **A scheduled run starts between ticking and confirming,** so `UninstallAsync` returns `null`. The user must see `Sweepbox is busy – the list will refresh when it finishes`, not nothing. Handled in Task 7. A Core test in Task 5 pins `null` when busy.
3. **Ticked items hidden by the search filter** still count in `Uninstall selected (n)` and still appear in the confirmation. Task 7: the count and the confirmation are computed from all items, not the visible ones.
4. **Tap-qualified Homebrew leaves** (`molsted/tap/sweeptool`) must get their version from `brew list --formula --versions`, which prints `sweeptool 0.3`. The uninstall uses the full name. Test in Task 1.
5. **Homebrew isn't installed, or a listing command fails.** A missing tool means no group and no error. A failing command shows that group's error while the other groups still list. Tests in Tasks 1 and 4.

---

### Task 1: Inventory types, probe helper, Homebrew listing and uninstall

Also split `Managers/PackageManager.cs` into one file per manager first: a pure move, no behaviour change.

**Files:**
- Modify: `src/Sweepbox.Core/Managers/PackageManager.cs` (keep only the abstract class)
- Create: `src/Sweepbox.Core/Managers/BrewManager.cs`, `NpmManager.cs`, `ChocoManager.cs` (moved classes)
- Create: `src/Sweepbox.Core/Managers/InstalledPackage.cs`
- Modify: `tests/Sweepbox.Tests/EngineTests.cs` (`FakeRunner`: add `ProbeExitCodes`, multi-line `ProbeAnswers`)
- Create: `tests/Sweepbox.Tests/InventoryTests.cs`

**Interfaces:**
- Produces, in `InstalledPackage.cs`:
  ```csharp
  public enum PackageKind { Cask, Formula, Package }
  public sealed record InstalledPackage(string ManagerId, string Name, string? Version, PackageKind Kind);
  public sealed record ManagerInventory(string ManagerId, string DisplayName, IReadOnlyList<InstalledPackage> Packages, string? Error);
  ```
- Produces, on `PackageManager`:
  - `public virtual Task<ManagerInventory> ListInstalledAsync(ICommandRunner runner, string executable, CancellationToken cancellationToken)`. The default returns an empty, error-free inventory.
  - `public virtual CommandSpec BuildUninstallStep(AppSettings settings, string executable, InstalledPackage package)`. The default throws `NotSupportedException`.
  - `public virtual bool IsProtected(string packageName)`. The default is `false`.
  - `protected sealed record ProbeOutput(int ExitCode, IReadOnlyList<string> Lines)` and `protected static Task<ProbeOutput> ProbeAsync(ICommandRunner runner, string executable, IReadOnlyDictionary<string,string> environment, CancellationToken cancellationToken, params string[] args)`. It uses a 60 s timeout and splits every emitted line on `'\n'`.
  - `protected ManagerInventory ListFailed(string command, ProbeOutput output)` → `Error = $"Couldn't list {ShortName} packages: \"{command}\" failed (exit {output.ExitCode})."`
  - Rewrite `QueryPathAsync` on top of `ProbeAsync`, keeping its 30 s timeout as a parameter.
- Produces, on `BrewManager`:
  - `internal static IReadOnlyList<InstalledPackage> ParseCasks(IEnumerable<string> lines)`
  - `internal static IReadOnlyList<InstalledPackage> ParseFormulae(IEnumerable<string> leaves, IEnumerable<string> versionLines)`

- [ ] **Step 1: Move `BrewManager`, `NpmManager` and `ChocoManager` into their own files.** Run `dotnet test Sweepbox.slnx`. Expected: PASS, 43 tests.

- [ ] **Step 2: Extend `FakeRunner`.** Add `public Dictionary<string,int> ProbeExitCodes { get; } = [];`. A probe returns `ProbeExitCodes.GetValueOrDefault(key, 0)`. A `ProbeAnswers` value may hold several lines separated by `\n`. Keep emitting it with one `onOutput` call, so the `'\n'` splitting in `ProbeAsync` gets exercised.

- [ ] **Step 3: Write the failing tests in `InventoryTests.cs`**

```csharp
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
```

- [ ] **Step 4: Run the new tests.**
  - Run: `dotnet test Sweepbox.slnx --filter "FullyQualifiedName~InventoryTests"`
  - Expected: FAIL, because `ParseCasks`, `ListInstalledAsync` and the other new members don't exist yet.

- [ ] **Step 5: Implement the interfaces above.**
  - `BrewManager.ListInstalledAsync` runs three probes: `list --cask --versions`, `leaves --installed-on-request` and `list --formula --versions`. On the first non-zero exit it returns `ListFailed("brew " + args, output)`. Otherwise it returns casks then formulae.
  - In `ParseFormulae`, look up versions by the part after the last `/`.
  - In `BuildUninstallStep`, use a new dictionary copied from `Env`, never the shared static one.

- [ ] **Step 6: Run the whole test suite.**
  - Run: `dotnet test Sweepbox.slnx`
  - Expected: PASS.

- [ ] **Step 7: Commit:** `git add -A src/Sweepbox.Core/Managers tests/Sweepbox.Tests && git commit -m "List installed Homebrew packages and build their uninstall steps"`

### Task 2: Chocolatey listing and uninstall

**Files:** Modify `src/Sweepbox.Core/Managers/ChocoManager.cs` and `tests/Sweepbox.Tests/InventoryTests.cs`.

**Interfaces:**
- Consumes: Task 1's `ProbeAsync`, `ListFailed`, `InstalledPackage` and `ManagerInventory`.
- Produces:
  - `internal static int? ParseMajorVersion(IEnumerable<string> lines)`
  - `internal static IReadOnlyList<InstalledPackage> ParseList(IEnumerable<string> lines)`, which applies the protected and variant filters
  - overrides of `ListInstalledAsync`, `BuildUninstallStep` and `IsProtected`

- [ ] **Step 1: Write the failing tests**

```csharp
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
public void Choco_protected_names(string name, bool expected) => Assert.Equal(expected, new ChocoManager().IsProtected(name));
```

- [ ] **Step 2: Run the tests.**
  - Run: `dotnet test Sweepbox.slnx --filter "FullyQualifiedName~InventoryTests"`
  - Expected: the new Chocolatey tests FAIL.

- [ ] **Step 3: Implement.**
  - `ListInstalledAsync` probes `--version` first.
    - If the major version is `null` or below 2, return the exact error from Global Constraints.
    - Otherwise probe `list --limit-output` and call `ParseList`.
  - Listing probes are never elevated.

- [ ] **Step 4: Run the whole test suite.**
  - Run: `dotnet test Sweepbox.slnx`
  - Expected: PASS.

- [ ] **Step 5: Commit:** `git commit -am "List installed Chocolatey packages and build their uninstall steps"`

### Task 3: npm listing and uninstall

**Files:** Modify `src/Sweepbox.Core/Managers/NpmManager.cs` and `tests/Sweepbox.Tests/InventoryTests.cs`.

**Interfaces:**
- Produces:
  - `internal static IReadOnlyList<InstalledPackage>? ParseGlobals(string json)`. It returns `null` for invalid JSON and an empty list when there's no `dependencies` object.
  - overrides of `ListInstalledAsync`, `BuildUninstallStep` and `IsProtected`

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Npm_globals_parse_and_skip_protected()
{
    var p = NpmManager.ParseGlobals("""{"dependencies":{"typescript":{"version":"5.6.3"},"npm":{"version":"10.9.0"},"corepack":{"version":"0.30.0"},"@angular/cli":{"version":"19.0.1"}}}""");
    Assert.Equal([("@angular/cli", "19.0.1"), ("typescript", "5.6.3")], p!.Select(x => (x.Name, x.Version)).OrderBy(x => x.Name));
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
public void Npm_uninstall_arguments()
{
    var step = new NpmManager().BuildUninstallStep(new AppSettings(), "npm", new("npm", "@angular/cli", null, PackageKind.Package));
    Assert.Equal(["uninstall", "--global", "@angular/cli"], step.Arguments);
    Assert.Equal("false", step.Environment["npm_config_fund"]);
}
```

- [ ] **Step 2: Run the tests.**
  - Run: `dotnet test Sweepbox.slnx --filter "FullyQualifiedName~InventoryTests"`
  - Expected: the new npm tests FAIL.

- [ ] **Step 3: Implement.**
  - Parse with `System.Text.Json`, catching `JsonException`.
  - `ListInstalledAsync` joins the lines with `"\n"` and parses them. It reports `ListFailed` only when `ParseGlobals` returns `null`, whatever the exit code.

- [ ] **Step 4: Run the whole test suite.**
  - Run: `dotnet test Sweepbox.slnx`
  - Expected: PASS.

- [ ] **Step 5: Commit:** `git commit -am "List global npm packages and build their uninstall steps"`

### Task 4: `PackageInventory` and confirmation notes

**Files:**
- Create: `src/Sweepbox.Core/Managers/PackageInventory.cs` and `src/Sweepbox.Core/Managers/UninstallNotes.cs`
- Test: `tests/Sweepbox.Tests/InventoryTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class PackageInventory(Func<AppSettings> settings, Func<AppSettings, Task<ICommandRunner>> runnerFactory, IReadOnlyList<PackageManager> managers)` with `public Task<IReadOnlyList<ManagerInventory>> ListAsync(CancellationToken cancellationToken)`.
    - It lists every manager that is supported on this OS and whose executable resolves, enabled or not, in the order of `managers`.
    - A non-cancellation exception from a manager becomes `ManagerInventory(..., [], ex.Message)`.
  - `public static class UninstallNotes` with `public static IReadOnlyList<string> For(AppSettings settings, IReadOnlyCollection<InstalledPackage> packages, bool isElevated)`, which returns the exact Global Constraints texts:
    - the Homebrew note when there's any `brew` package and `Brew.Autoremove` is on;
    - the Chocolatey note when there's any `choco` package, `Choco.ElevateWithUac` is on and `!isElevated`.

- [ ] **Step 1: Write the failing tests**
  - `Inventory_lists_disabled_managers_and_skips_missing_ones`:
    - Settings: `_settings.Npm.Enabled = false`. Runner: `Installed.Remove("brew")`. Managers: `[Brew, Npm]`.
    - Assert that one inventory is returned, `"npm"`, with the typescript package from the probe answer.
  - `Inventory_keeps_other_managers_when_one_throws`:
    - A test-only `PackageManager` subclass whose `ListInstalledAsync` throws `InvalidOperationException("boom")`, plus npm.
    - Assert that both inventories are returned, the first with `Error == "boom"`, and npm with packages.
  - `Notes_follow_settings_and_elevation`:
    - Assert all four combinations, including an empty list when there are only npm packages.

- [ ] **Step 2: Run the tests.**
  - Run: `dotnet test Sweepbox.slnx --filter "FullyQualifiedName~InventoryTests"`
  - Expected: FAIL.

- [ ] **Step 3: Implement both types.**

- [ ] **Step 4: Run the whole test suite.**
  - Run: `dotnet test Sweepbox.slnx`
  - Expected: PASS.

- [ ] **Step 5: Commit:** `git commit -am "Add PackageInventory and uninstall confirmation notes"`

### Task 5: Uninstall runs in the engine, kept out of scheduling and the Overview

**Files:**
- Modify: `src/Sweepbox.Core/Engine/RunReport.cs` (`RunTrigger.Uninstall`)
- Modify: `src/Sweepbox.Core/Engine/RunHistoryStore.cs`, `MaintenanceEngine.cs` and `Scheduler.cs`
- Test: `tests/Sweepbox.Tests/EngineTests.cs`

**Interfaces:**
- Produces:
  - `RunTrigger.Uninstall`, appended last so existing history JSON still parses.
  - `RunHistoryStore.LatestMaintenance`: the newest run whose `Trigger != RunTrigger.Uninstall`.
  - `public Task<RunReport?> MaintenanceEngine.UninstallAsync(IReadOnlyList<InstalledPackage> packages)`. It throws `ArgumentException` when the list is empty, and returns `null` when a run is in progress.
- Refactor: extract the body of `RunAsync` into a private `ExecuteAsync(RunTrigger trigger, Func<AppSettings, IEnumerable<PackageManager>> selectManagers, Func<PackageManager, AppSettings, string, IReadOnlyList<CommandSpec>> buildSteps)`.
  - `RunAsync` passes the current selection and `m.BuildSteps(s, exe)`.
  - `UninstallAsync` passes the supported managers that have packages, in `_managers` order, and `packages.Where(p => p.ManagerId == m.Id && !m.IsProtected(p.Name)).Select(p => m.BuildUninstallStep(s, exe, p))`. The existing cleanup measurement doesn't trigger, because no uninstall step has `IsCleanup`.
- `EngineStatus.LastRun`:
  - initial value `history.LatestMaintenance`;
  - after a run, `trigger == Uninstall ? previous LastRun : report`.
- `Scheduler.NextRunAt` uses `_history.LatestMaintenance?.StartedAt`.

- [ ] **Step 1: Write the failing tests in `EngineTests.cs`**

```csharp
[Fact]
public async Task Uninstall_runs_one_step_per_package_and_continues_after_a_failure()
{
    _runner.ExitCode = s => s.Arguments.Contains("zoom") ? 1 : 0;
    var report = await Engine().UninstallAsync([
        new("brew", "zoom", "6.2.11", PackageKind.Cask), new("brew", "wget", "1.25.0", PackageKind.Formula),
        new("npm", "typescript", "5.6.3", PackageKind.Package)]);
    Assert.Equal(RunTrigger.Uninstall, report!.Trigger);
    Assert.Equal(["Uninstall zoom", "Uninstall wget"], report.Managers[0].Steps.Select(s => s.Description));
    Assert.Equal(Outcome.Failed, report.Managers[0].Outcome);
    Assert.Equal(Outcome.Succeeded, report.Managers[1].Outcome);
    Assert.Null(report.BytesFreed);
    Assert.Equal(3, _runner.Ran.Count);
}

[Fact]
public async Task Uninstall_returns_null_while_another_run_is_in_progress()
{
    _runner.Gate = new();
    var engine = Engine();
    var running = engine.RunAsync(RunTrigger.Manual);
    Assert.Null(await engine.UninstallAsync([new("npm", "typescript", null, PackageKind.Package)]));
    _runner.Gate.SetResult();
    await running;
}

[Fact]
public async Task Uninstall_never_runs_protected_packages()
{
    await Engine().UninstallAsync([new("npm", "npm", null, PackageKind.Package), new("npm", "typescript", null, PackageKind.Package)]);
    Assert.Equal(["uninstall --global typescript"], _runner.Ran.Select(s => string.Join(' ', s.Arguments)));
}

[Fact]
public async Task Uninstall_does_not_replace_last_run_or_move_the_schedule()
{
    var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero));
    var engine = Engine(time);
    var maintenance = await engine.RunAsync(RunTrigger.Scheduled);
    time.Advance(TimeSpan.FromHours(5));
    await engine.UninstallAsync([new("npm", "typescript", null, PackageKind.Package)]);
    Assert.Same(maintenance, engine.Status.LastRun);
    Assert.Equal(maintenance!.StartedAt, _history.LatestMaintenance!.StartedAt);
    Assert.Equal(RunTrigger.Uninstall, _history.Latest!.Trigger);
    var scheduler = new Scheduler(engine, () => _settings, _history, time);
    Assert.Equal(maintenance.StartedAt + TimeSpan.FromHours(24), scheduler.NextRunAt);
}
```

Also add `Cancelling_an_uninstall_marks_remaining_managers_cancelled`, modelled on `Second_run_while_running_is_rejected_and_cancel_stops_the_run`:
1. Set `_runner.Gate`.
2. Start `UninstallAsync` with one brew package and one npm package.
3. Call `engine.Cancel()`.
4. Assert `report.Outcome == Outcome.Cancelled`, `report.Managers[1].Outcome == Outcome.Cancelled`, and that the npm step never ran.

- [ ] **Step 2: Run the tests.**
  - Run: `dotnet test Sweepbox.slnx --filter "FullyQualifiedName~EngineTests"`
  - Expected: the new tests FAIL.

- [ ] **Step 3: Implement the refactor and the new members.**

- [ ] **Step 4: Run the whole test suite.**
  - Run: `dotnet test Sweepbox.slnx`
  - Expected: PASS, including every existing engine and scheduler test, unchanged.

- [ ] **Step 5: Commit:** `git commit -am "Run uninstalls through the engine without affecting schedule or last run"`

### Task 6: Wire inventory and uninstall into `AppServices`

**Files:** Modify `src/Sweepbox.Desktop/Services/AppServices.cs`.

**Interfaces:**
- Consumes: `PackageInventory` (Task 4), `UninstallAsync` (Task 5).
- Produces:
  - `public PackageInventory Inventory { get; }`, built with the same settings getter, runner factory and managers as the engine.
  - `public Task<RunReport?> UninstallAsync(IReadOnlyList<InstalledPackage> packages)`
  - A new optional constructor parameter, `Func<AppSettings, Task<ICommandRunner>>? runnerFactory = null`. The default is the current `ProcessCommandRunner` factory. `tools/Screenshots` uses this parameter in Task 8.
- `Notify` uses `Uninstall failed` / `Uninstall finished` when `report.Trigger == RunTrigger.Uninstall`, and the existing maintenance titles otherwise, under the same settings.

- [ ] **Step 1: Implement.**

- [ ] **Step 2: Build and test.**
  - Run: `dotnet build Sweepbox.slnx && dotnet test Sweepbox.slnx`
  - Expected: the build succeeds with 0 warnings and the tests PASS.

- [ ] **Step 3: Commit:** `git commit -am "Expose package inventory and uninstall from AppServices"`

### Task 7: The Installed tab and the confirmation dialog

**Files:**
- Create: `src/Sweepbox.Desktop/ViewModels/InstalledViewModel.cs` (with `PackageGroup` and `PackageItem`)
- Create: `src/Sweepbox.Desktop/Views/UninstallConfirmDialog.axaml(.cs)`
- Modify: `src/Sweepbox.Desktop/ViewModels/StatusViewModel.cs`, `Views/StatusWindow.axaml`, `Views/StatusWindow.axaml.cs`

**Interfaces:**
- Consumes: `AppServices.Inventory`, `AppServices.UninstallAsync`, `UninstallNotes.For`, `Engine.StatusChanged` and `Engine.RunCompleted`.
- Produces:
  - `InstalledViewModel(AppServices services, Action showLog)`, which is `IDisposable`.
    - Members: `ObservableCollection<PackageGroup> Groups`, `string SearchText`, `bool IsLoading`, `bool IsBusy`, `int SelectedCount`, `string UninstallLabel` (`Uninstall selected ({n})`).
    - Commands: `RefreshCommand` and `UninstallSelectedCommand`. Both can run only when `!IsBusy && !IsLoading`, and uninstall also needs `SelectedCount > 0`.
    - `Task EnsureLoadedAsync()`, which loads once per view model lifetime.
    - `Func<IReadOnlyList<InstalledPackage>, IReadOnlyList<string>, Task<bool>>? Confirm`, set by the window.
  - `PackageGroup`: `Title`, `Count`, `Error`, `HasError` and `Items`.
  - `PackageItem`: an observable `IsChecked` and `IsVisible`, plus `Package`, `Name` and `Version`.
  - `StatusViewModel.Installed` (`InstalledViewModel`) and `[ObservableProperty] int SelectedTab`. Tab indices: 0 = Overview, 1 = Installed, 2 = Live log.

Behaviour (from the spec, Desktop part):
- **Groups:** one per (manager, kind), titled exactly as in Global Constraints, in the order Homebrew casks, Homebrew formulae, Chocolatey, npm. Items are sorted case-insensitively. A manager with `Error` shows one group with the error text and no items.
- **Search** sets `IsVisible` per item by case-insensitive substring of the name. A group with no visible items, and no error, is hidden. `SelectedCount` counts every checked item, visible or not.
- **Uninstall:**
  1. Collect every checked item and call `Confirm(packages, UninstallNotes.For(settings, packages, Environment.IsPrivilegedProcess))`. On `false`, stop.
  2. Clear the log the way `RunNow` does, switch to tab 2 (`showLog`), and `await services.UninstallAsync(...)`.
  3. If it returns `null`, set the busy message and stay on the current list.
- **Busy:** `IsBusy` mirrors `Engine.Status.IsRunning`, posted to the UI thread. While it is true, the tab shows `Sweepbox is busy – the list will refresh when it finishes`.
- **Refresh:** on `RunCompleted` (any trigger), post a reload if the list was loaded before. A reload rebuilds the groups, which clears all ticks.
- **Loading:** `EnsureLoadedAsync` is called from `OnSelectedTabChanged(1)`. `Dispose` cancels any listing in progress and unsubscribes from the engine events. `StatusViewModel.Dispose` disposes `Installed`.
- **Dialog:** `UninstallConfirmDialog` has a parameterless constructor and a `DataContext` record `(IReadOnlyList<string> Lines, IReadOnlyList<string> Notes)`. Each line reads `"{Name} ({group title})"`. The **Uninstall** button closes with `true`. **Cancel** is `IsDefault` and `IsCancel` and closes with `false`. Follow `FirstRunDialog`'s style.
- **Window:**
  - `StatusWindow` code-behind sets `vm.Installed.Confirm = (p, n) => new UninstallConfirmDialog { DataContext = … }.ShowDialog<bool>(this)` once its `DataContext` is set.
  - In XAML, the `TabControl` gets `SelectedIndex="{Binding SelectedTab}"`, and a new `TabItem Header="Installed"` sits between Overview and Live log.
  - The tab contains: the search `TextBox` and Refresh button at the top; a `ScrollViewer` with an `ItemsControl` of groups (an `Expander` per group, header `Title` plus `Count`, items as `CheckBox` with `Name` and a muted `Version`); a loading `ProgressBar` and the busy text; and the uninstall button docked at the bottom.
  - Every template has `x:DataType`.

- [ ] **Step 1: Implement the view models, the dialog and the XAML.**

- [ ] **Step 2: Build.**
  - Run: `dotnet build Sweepbox.slnx`
  - Expected: success, 0 warnings.

- [ ] **Step 3: Check it by hand.**
  1. Quit any installed Sweepbox, then run `dotnet run --project src/Sweepbox.Desktop` and open Status → Installed.
  2. Expected: groups load with versions. Search filters them. Ticking items updates the count, and the count stays the same when the search hides ticked items.
  3. Tick one harmless package, for example a throwaway `npm i -g cowsay` installed first. Confirm.
  4. Expected: the window switches to Live log, the log shows `=== Uninstall run started`, the list refreshes without the package, the history row says `Uninstall`, and the Overview headline is unchanged.
  5. Click Cancel in the dialog. Expected: nothing runs.

- [ ] **Step 4: Commit:** `git add -A src/Sweepbox.Desktop && git commit -m "Add the Installed tab with uninstall to the Status window"`

### Task 8: Screenshots and documentation

**Files:**
- Modify: `tools/Screenshots/Program.cs` and `tools/frame_screenshots.py`
- Modify: `user-docs/MANUAL.md`, `README.md` and `docs/architecture.md`
- Generated: `user-docs/images/installed-{macos,windows}[-dark].png`, plus the regenerated status images

- [ ] **Step 1: Update `tools/Screenshots`.**
  - In `SampleData.Create`, pass a fake runner factory to `AppServices`: a small `ICommandRunner` in the tool that resolves every executable and answers the listing probes with canned output. On macOS: about 6 casks and 8 formulae. On Windows: about 8 Chocolatey packages, including `git`/`git.install`. Both: 3 npm globals.
  - Change the Live log shot to `selectTab: 2`.
  - Add `Shot(..., $"installed-{platform}{suffix}", selectTab: 1)`. Before it, `await vm.Installed.EnsureLoadedAsync()` and tick two items, so the button shows `Uninstall selected (2)`.

- [ ] **Step 2: Add `"installed": "Sweepbox"` to `TITLES` in `tools/frame_screenshots.py`.**

- [ ] **Step 3: Regenerate the screenshots.**
  - Run: `dotnet run --project tools/Screenshots -- user-docs/images/raw && python3 tools/frame_screenshots.py`
  - Expected: the `installed-*.png` files exist. Open one and check that the groups, versions and button label render.

- [ ] **Step 4: Update the docs.**
  - **`user-docs/MANUAL.md`:** add an "Installed packages" subsection under "The status window", with the light/dark `<picture>` like the existing ones. Cover:
    - what is listed, and why Homebrew dependencies and protected packages aren't;
    - how to search, tick and confirm;
    - the Homebrew autoremove behaviour;
    - the Chocolatey UAC prompts and the 2.x requirement;
    - that results appear in the Live log and history.
  - **`README.md`:** add a feature bullet.
  - **`docs/architecture.md`:** document listing (`ListInstalledAsync`, `PackageInventory`), uninstall runs (`UninstallAsync`, `RunTrigger.Uninstall`) and `LatestMaintenance`, plus the new tab in the Desktop table.

- [ ] **Step 5: Verify.**
  - Run: `dotnet test Sweepbox.slnx -c Release -p:RestoreLockedMode=true`
  - Expected: PASS.

- [ ] **Step 6: Commit:** `git add -A user-docs README.md docs/architecture.md tools && git commit -m "Document the Installed tab and add its screenshots"`
