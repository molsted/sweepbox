# Installed Sizes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show each installed package's disk usage (exact, `≈` estimate, or `–`) and per-group totals on the Installed tab, with a Name/Size sort. Sizes are measured in the background per manager.

**Architecture:**
- Each `PackageManager` gets `MeasureAsync`, built on pure parsers and an extended `DiskUsage`.
- `PackageInventory.MeasureAsync` reports results per manager through a callback.
- `InstalledViewModel` measures after every load, with its own cancellation and a generation guard, and updates rows on the UI thread.

**Tech Stack:** .NET 10 (SDK 10.0.301), Avalonia 12.2, CommunityToolkit.Mvvm, xUnit 2, System.Text.Json, Microsoft.Win32.Registry (Windows only).

**Spec:** `docs/superpowers/specs/2026-11-26-installed-sizes-design.md`. Read it in full. The table and the "Calls made after the builder review" section are binding.

## Global Constraints

- **Three outcomes:** a size is exact, an estimate (`≈`), or unknown (`–`). Never show a guess as exact.
- **Chocolatey:** never count `lib\<id>`. Its only source is the registry `EstimatedSize` (KB, positive DWORD) of a single matched entry.
  - Skip entries with `SystemComponent = 1` or a `ParentKeyName`.
  - Collapse duplicates by normalized `DisplayName`.
  - Exact normalized match first. Otherwise a prefix match at a boundary (space, digit, `(`, `-`, `_`, `.` after the prefix in the original name). More than one distinct candidate means no estimate.
- **Casks:**
  - Exact only if the cask has only `app` artifacts and every app path exists.
  - Otherwise an estimate from the union, deduplicated by full path, of: the existing app paths; existing `uninstall.delete` paths (with `~` and `$HOME` expanded, and paths with wildcards skipped); and the files of literal `pkgutil` receipt ids (ids with regex characters such as `*`, `?`, `[`, `\` skipped). Plus `Caskroom/<token>`.
  - If nothing is found: unknown.
- **Formulae:** `Cellar/<name after the last '/'>`.
- **npm:** `<npm root -g>/<name>`.
- **Probes:** `IsProbe`, 60 s timeout, the manager's environment. Cut JSON from the first `{` to the last `}`. If the multi-token `brew info --json=v2 --cask` call fails, retry per token.
- **Keys:** dictionaries are keyed by `PackageKey(ManagerId, Name, Kind)`, never by `InstalledPackage`.
- **Measuring never blocks the UI:** it never sets `IsLoading` and never disables Refresh or Uninstall. A reload, Refresh, uninstall or Dispose cancels it. Stale results are discarded.
- **UI text, exact:**
  - `Measuring sizes…`
  - sort labels `Name` and `Size`
  - size text `1.2 GB` (from `DiskUsage.Format`), `≈ 1.2 GB` or `–`
  - group total `≈ ` prefix when any part of it is an estimate
- **Build and tests:** 0 warnings on both Desktop targets (`net10.0`, `net10.0-windows10.0.19041.0`). Lock files stay unchanged unless a package is added; none should be.

## Review Focus

1. A huge folder (Xcode-sized cask) is measured while the user clicks Refresh. Measuring must stop promptly, and its results must not land in the new list. Tested in Task 1 (cancellation inside `DiskUsage`) and Task 6 (generation guard).
2. One stale or renamed cask must not blank every cask's size (per-token retry, plus `old_tokens` matching). Tested in Task 3.
3. Homebrew prints warnings around its JSON. Test in Task 3 with surrounding warning text.
4. A Chocolatey package must never get a similarly named program's size ("GitHub Desktop" for `git`). Tested in Task 4.
5. `pkgutil --files` lists directories and paths nested inside an app path, so the same bytes must not be counted twice. Tested in Tasks 1 and 3.

---

### Task 1: Size primitives and DiskUsage extensions

**Files:**
- Create: `src/Sweepbox.Core/Managers/PackageSize.cs`, holding `PackageSize` and `PackageKey`.
- Modify: `src/Sweepbox.Core/Running/DiskUsage.cs`.
- Test: create `tests/Sweepbox.Tests/SizeTests.cs`.

**Interfaces (produced):**
- `public sealed record PackageSize(long Bytes, bool IsEstimate);`
- `public sealed record PackageKey(string ManagerId, string Name, PackageKind Kind)`, with `public static PackageKey Of(InstalledPackage p)`.
- `public static long DiskUsage.Measure(IEnumerable<string> directories, CancellationToken cancellationToken)`. The existing overload delegates to it with `CancellationToken.None`. It throws `OperationCanceledException` when cancelled, checked at least every 1,000 files.
- `public static long DiskUsage.MeasurePaths(IEnumerable<string> paths, CancellationToken cancellationToken)`. Each path may be a file or a directory, and missing paths are ignored.
  - Paths are normalized with `Path.GetFullPath`, with any trailing separator trimmed, then deduplicated.
  - A path that lies inside another listed directory is skipped.
  - Symlinked files and directories are skipped, as `Measure` already does.

- [ ] **Step 1:** Write failing tests in `SizeTests`:
  - `MeasurePaths_counts_files_and_directories_once`: a directory with two files (100 + 50 bytes), the same directory listed twice, one of its files listed separately, and a separate 30-byte file. Expected: 180.
  - `MeasurePaths_ignores_missing_paths`: expected 0.
  - `Measure_stops_when_cancelled`: a directory with 3,000 tiny files and a pre-cancelled token. Expected: `Assert.Throws<OperationCanceledException>`.
  - `PackageKey_ignores_companions`: `PackageKey.Of(new("choco","git","1",Package,["git.install"]))` equals `PackageKey.Of(new("choco","git","2",Package))`.
- [ ] **Step 2:** Run `dotnet test Sweepbox.slnx --filter FullyQualifiedName~SizeTests`. Expected: a compile failure.
- [ ] **Step 3:** Implement the interfaces.
- [ ] **Step 4:** Run the same filter, then the full suite. Expected: PASS, with 0 warnings.
- [ ] **Step 5:** Commit: "Add package size types and path-aware, cancellable disk measuring".

### Task 2: Measuring Homebrew formulae and npm packages; the manager hook

**Files:** modify `PackageManager.cs`, `BrewManager.cs` and `NpmManager.cs`. Test: `SizeTests.cs`.

**Interfaces:**
- Produces `public virtual Task<IReadOnlyDictionary<PackageKey, PackageSize?>> MeasureAsync(ICommandRunner runner, string executable, IReadOnlyList<InstalledPackage> packages, CancellationToken cancellationToken)` on `PackageManager`. The default maps every package to `null`.
- `BrewManager.MeasureAsync` handles formulae only in this task. Casks map to `null` until Task 3.
  - Probe `--prefix`, then measure `Cellar/<last segment of name>` with `DiskUsage.Measure` (exact).
  - A missing folder or a failed probe gives `null`.
- `NpmManager.MeasureAsync` probes `root -g` (the last absolute line) and measures `<root>/<name>`. Scoped names (`@scope/pkg`) map to nested folders.

- [ ] **Step 1:** Write failing tests, using `FakeRunner.ProbeAnswers` and temp folders:
  - `Brew_formula_size_is_its_cellar_folder`: `ProbeAnswers["--prefix"] = tempPrefix`, with `Cellar/wget/1.25.0/bin/wget` at 1,000 bytes, and the package `molsted/tap/wget`. Expected: `new PackageSize(1000, false)`.
  - `Npm_size_is_its_global_folder_including_scoped`: `ProbeAnswers["root -g"] = tempRoot`, with `@angular/cli` at 300 bytes and `typescript` at 200. Expected: both sizes, exact.
  - `Missing_folder_or_failed_probe_is_unknown`.
  - `Default_manager_reports_unknown`.
- [ ] **Step 2:** Run them. Expected: they fail.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the filter, then the full suite. Expected: pass.
- [ ] **Step 5:** Commit: "Measure Homebrew formula and global npm package sizes".

### Task 3: Homebrew cask sizes

**Files:** modify `BrewManager.cs` (optionally a new `BrewCaskSizes.cs` beside it for the parsers). Test: `SizeTests.cs`.

**Interfaces:**
- Produces `internal sealed record CaskFootprint(string Token, IReadOnlyList<string> AppPaths, bool AppsOnly, IReadOnlyList<string> DeletePaths, IReadOnlyList<string> PkgIds, IReadOnlyList<string> Aliases)`.
  - `Aliases` holds `full_token` and `old_tokens`.
  - `AppPaths` uses the artifact's `target` when it's given, otherwise `/Applications/<app>`.
  - `DeletePaths` comes from `uninstall` → `delete` (a string or an array), with `~` and `$HOME` expanded and paths containing `*`, `?` or `[` dropped.
  - `PkgIds` comes from `uninstall` → `pkgutil` (a string or an array), with ids containing regex characters dropped.
  - `AppsOnly` is true when every artifact entry is an `app`, ignoring metadata entries such as `uninstall_postflight`. Treat any `pkg`, `installer` or `uninstall` artifact as not apps-only.
- Produces `internal static IReadOnlyList<CaskFootprint> ParseCaskInfo(string json, string home)`, which reads the `casks` array.
- Produces `internal static string? ParsePkgRoot(IEnumerable<string> pkgInfoLines)`. It joins the `volume:` and `location:` values into an absolute prefix; an empty location means the volume root.
- `BrewManager.MeasureAsync` for casks:
  1. Run one `info --json=v2 --cask <tokens…>` call (JSON cut from `{` to `}`). If it fails, run one call per token and skip the ones that still fail.
  2. Map each footprint to a package by `Token`, or by `Aliases`.
  3. For each package:
     - apps-only and every app path exists → `MeasurePaths(apps + Caskroom/<token>)`, exact;
     - otherwise → build the union (existing apps, existing delete paths, receipt files) plus Caskroom and measure it with `MeasurePaths`. If the union, without Caskroom, has no existing path, the result is `null`; otherwise it's an estimate.
  4. Receipt files: probe `pkgutil --pkg-info <id>` and `--files <id>` through `runner.ResolveExecutable("pkgutil")`, only when `OperatingSystem.IsMacOS()` (tests run on macOS in CI). Combine each listed path with the root, keep it only if it's a regular file, and let `MeasurePaths` deduplicate.

- [ ] **Step 1:** Write failing tests. Store JSON fixtures as raw string literals in the test, shaped like real `brew info --json=v2` output.
  - `ParseCaskInfo` cases:
    - an app cask with a `target`;
    - an app cask without one;
    - a pkg cask (Zoom-like: `uninstall` holding `pkgutil` and `delete`, with one wildcard path);
    - a mixed cask (an `app` plus `uninstall`);
    - a `font-*` cask (a `font` artifact, which is neither apps-only nor carrying delete paths);
    - `old_tokens`.
  - `ParsePkgRoot`: `volume: /` and `location: Applications` give `/Applications`; an empty location gives `/`.
  - `MeasureAsync` cases, with a temp folder standing in for `/Applications` (use `target` paths in the fixture):
    - app-only exact;
    - app missing → estimate or unknown;
    - pkg cask estimate, where the receipt lists a directory plus files, one of them nested in a delete path. It must be counted once (FakeRunner gets `"pkgutil"` in `Installed`, plus `ProbeAnswers` for `--pkg-info id` and `--files id`).
    - a batch call failing (`ProbeExitCodes`) falls back to per-token calls, and a stale token is skipped;
    - warning lines around the JSON are ignored.
- [ ] **Step 2:** Run them. Expected: they fail.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the filter, then the full suite. Expected: pass.
- [ ] **Step 5:** Commit: "Measure Homebrew cask sizes from apps, uninstall paths and package receipts".

### Task 4: Chocolatey estimates from "Apps & features"

**Files:**
- Create `src/Sweepbox.Core/Platform/InstalledPrograms.cs`, holding `IInstalledProgramsSource`, `InstalledProgram` and `RegistryInstalledPrograms`.
- Modify `ChocoManager.cs`. Test: `SizeTests.cs`.

**Interfaces:**
- Produces `public sealed record InstalledProgram(string DisplayName, long? EstimatedSizeKb, bool SystemComponent, string? ParentKeyName);`
- Produces `public interface IInstalledProgramsSource { IReadOnlyList<InstalledProgram> Read(); }`
- `RegistryInstalledPrograms` is `[SupportedOSPlatform("windows")]`. It reads `SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` from HKLM in the 64- and 32-bit views and from HKCU. It never throws; on failure it writes to `Trace` and returns an empty list.
- `ChocoManager` gains a constructor that takes an optional `IInstalledProgramsSource? programs` (the default is the registry source on Windows, and none elsewhere).
- Produces `internal static long? MatchEstimatedSizeKb(string packageId, IReadOnlyList<InstalledProgram> programs)`, implementing the matching rules in Global Constraints exactly.
- `ChocoManager.MeasureAsync` returns `new PackageSize(kb * 1024, true)`, or `null`.

- [ ] **Step 1:** Write failing tests for `MatchEstimatedSizeKb`:
  - `git` vs ["Git", "GitHub Desktop", "Git LFS"] → Git's size (exact match wins);
  - `git` vs ["GitHub Desktop"] → null;
  - `vlc` vs ["VLC media player"] → its size;
  - `7zip.install` vs ["7-Zip 24.09 (x64)"] → its size (the normalized prefix `7zip` is followed by a space and a digit in the original name);
  - a duplicate "Git" in HKLM and HKCU → counted once;
  - two distinct prefix candidates → null;
  - an entry with `SystemComponent` or `ParentKeyName` → ignored;
  - a missing or zero `EstimatedSize` → ignored;
  - `ChocoManager.MeasureAsync` with a fake source → an estimate in bytes; unmatched packages → null.
- [ ] **Step 2:** Run them. Expected: they fail.
- [ ] **Step 3:** Implement. Build both targets: the registry class compiles in `net10.0` Core under the platform attribute, with no CA1416 warnings.
- [ ] **Step 4:** Run the full suite and a solution build. Expected: pass, with 0 warnings.
- [ ] **Step 5:** Commit: "Estimate Chocolatey package sizes from Apps & features".

### Task 5: `PackageInventory.MeasureAsync`

**Files:** modify `PackageInventory.cs`. Test: `InventoryTests.cs`.

**Interfaces:**
- Produces `public Task MeasureAsync(IReadOnlyList<ManagerInventory> inventories, Action<IReadOnlyDictionary<PackageKey, PackageSize?>> onManagerMeasured, CancellationToken cancellationToken)`.
  - It resolves the runner once, as `ListAsync` does, and walks the inventories in order, skipping those with no packages.
  - It calls the manager's `MeasureAsync` and invokes the callback once per manager.
  - A non-cancellation exception writes to `Trace` and invokes the callback with all `null` for that manager. Cancellation propagates.

- [ ] **Step 1:** Write failing tests:
  - `Measure_reports_each_manager_separately`: two managers, two callback invocations, in order.
  - `Measure_isolates_a_failing_manager`: a throwing test manager gets `null`s, and the other still reports.
  - `Measure_propagates_cancellation`.
- [ ] **Step 2:** Run them. Expected: they fail.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run them. Expected: pass.
- [ ] **Step 5:** Commit: "Measure installed package sizes per manager".

### Task 6: Installed tab UI

**Files:** modify `src/Sweepbox.Desktop/ViewModels/InstalledViewModel.cs` and `src/Sweepbox.Desktop/Views/StatusWindow.axaml`.

**Interfaces and behaviour:**
- **`PackageItem`:**
  - `[ObservableProperty] PackageSize? _size;` and `[ObservableProperty] bool _sizeKnown;` (false until its manager reports).
  - `public string SizeText`: an empty string until reported; `–` when null; `≈ {Format}` for an estimate; otherwise `{Format}`.
- **`PackageGroup.TotalText`:**
  - empty until every item in the group has been reported;
  - otherwise the sum of the known sizes, with a `≈ ` prefix if any of them is an estimate;
  - empty if every size is null.
  - It updates when its items' sizes change.
- **`InstalledViewModel`:**
  - `[ObservableProperty] bool _isMeasuring;`, `[ObservableProperty] bool _sortBySize;`, and `int _generation`, plus its own `CancellationTokenSource _measureCts`.
  - After a successful load, it starts measuring and applies each callback on the UI thread, but only if the generation is unchanged.
  - The measure CTS is cancelled at the start of every load, on Refresh, at the start of an uninstall, and in Dispose.
- **Sorting:** within each group, by size (largest first; unreported and null sizes last, then by name) when `SortBySize` is on, otherwise by name. Re-sort as results arrive and when the toggle changes. Sorting must keep the existing `PackageItem` instances, so ticks are preserved.
- **XAML:**
  - a size `TextBlock` per row, right-aligned and muted;
  - the group total after the count in the expander header;
  - a "Sort:" label with two radio-style toggle buttons, `Name` and `Size`, next to Refresh;
  - `Measuring sizes…` (muted) next to the search box while `IsMeasuring` is true.
  - Every template keeps its `x:DataType`.

- [ ] **Step 1:** Implement.
- [ ] **Step 2:** Build with `dotnet build Sweepbox.slnx -c Release -p:RestoreLockedMode=true`. Expected: 0 warnings, both targets.
- [ ] **Step 3:** Smoke test: `dotnet run --project src/Sweepbox.Desktop -f net10.0 -- --dry-run` exits 0. Don't launch the GUI and don't install anything; the controller does the manual pass with the user.
- [ ] **Step 4:** Commit: "Show package sizes, group totals and size sorting on the Installed tab".

### Task 7: Screenshots and documentation

**Files:** modify `tools/Screenshots/Program.cs`, `user-docs/MANUAL.md` and `docs/architecture.md`. Regenerate `user-docs/images/installed-*.png`.

- [ ] **Step 1: Sample data.** In `tools/Screenshots`, give the fake runner sample sizes.
  - Simplest is a test-only `PackageManager` wrapper (`OnAnyOS` already exists) that overrides `MeasureAsync` with fixed sizes: a mix of exact, `≈` and unknown on both platforms.
  - Before taking the Installed shot, wait until measuring finishes.
- [ ] **Step 2: Regenerate the images.**
  - Run `dotnet run --project tools/Screenshots -c Release -- user-docs/images/raw` and the scratch venv's `python tools/frame_screenshots.py`.
  - Check one installed image: it should show sizes, totals and the sort control.
  - Also rerun `tools/generate_site_graphics.py` only if `status-macos*.png` changed. They shouldn't.
- [ ] **Step 3: Docs.**
  - **MANUAL**, Installed packages section: what the size column means, `≈` and `–` (with examples of what gets which), that formula sizes exclude dependencies, the sort control, and that sizes appear a moment after the list.
  - **`docs/architecture.md`:** a size-measuring paragraph (per-manager sources, `PackageKey`, the generation guard).
- [ ] **Step 4:** Build the site with the scratch venv's `python tools/build_site.py`, then `rm -rf _site`. Run the full build and test with `RestoreLockedMode`. Expected: 0 warnings, all tests pass.
- [ ] **Step 5:** Commit: "Document package sizes and refresh the Installed screenshots".
