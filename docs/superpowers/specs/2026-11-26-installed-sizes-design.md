# Design: Disk usage on the Installed tab

Date: 2026-11-26 · Status: draft for approval

## Intent

The Installed tab exists so the user can remove apps and tools they no longer need. Showing how much disk space each package takes lets them see which removals are worth it, and find the big ones quickly.

## Goals

- Each package row shows its size on disk, or `≈ size` when the number is an estimate, or `–` when Sweepbox can't tell.
- Each group header shows the group's total. It is marked `≈` if any part of it is an estimate.
- A sort control ("Name" / "Size") on the tab, with Name as the default. Size sorts largest first, and unknown sizes go last.
- Sizes are measured in the background after the list appears, so the list is never slower than today. While sizes are being measured, rows show nothing in the size column and the tab shows `Measuring sizes…`.

## Non-goals

- Exact accounting of every file a package scattered over the system (caches, preferences, support files under `~/Library`).
- Space freed by an uninstall (still a non-goal from the uninstall design).
- Caching sizes between app starts.
- Showing sizes anywhere other than the Installed tab.

## Anti-goals

- **Presenting a guess as exact.** Anything approximate carries `≈`.
- **Slowing down or blocking the list,** or making Refresh/Uninstall wait for size measurement.
- **Wrong attribution:** a Chocolatey package getting another program's size because a name match was too loose.

## Approach (chosen by the user: B with A as fallback)

Measure exactly where a package owns its own folder. Where it doesn't, fall back to the system's own record and mark it `≈`. If neither exists, show `–`.

| Manager / kind | Exact | Estimate (`≈`) | Unknown (`–`) |
|---|---|---|---|
| Homebrew formula | `<prefix>/Cellar/<name>` (all versions; a tap-qualified `user/tap/name` uses `name`) | – | folder missing |
| npm global | `<npm root -g>/<name>` (scoped names included) | – | folder missing |
| Homebrew cask, `app` artifacts only | every app path exists (the artifact's `target` if given, else `/Applications/<app>`): their sizes plus `<prefix>/Caskroom/<token>` | – | – |
| Homebrew cask, anything else (pkg installer, mixed `app` + `pkg`/`uninstall`, or an app that is missing) | – | the union, deduplicated by full path, of: existing app paths; existing `uninstall` → `delete` paths (`~` and `$HOME` expanded; paths with wildcards skipped); and the files of each literal `pkgutil` receipt id (ids with regex characters skipped). Plus Caskroom. | nothing found, which also covers `font-*`, `binary`-only, `suite`, `qlplugin` and similar casks |
| Chocolatey | – | the `EstimatedSize` of the one "Apps & features" entry that matches (see below) | no entry matches, or several distinct ones do |

**Calls made after the builder review** (controller's calls, within the user's "B with A as fallback"):

- **Chocolatey never adds the `lib` folder.** For installer packages, that folder usually holds the vendor's installer, so adding it would double count or present an installer's size as the installed size. Portable tools without an "Apps & features" entry therefore show `–`. They're small, and an honest `–` beats a misleading number.
- **Chocolatey matching:**
  - Normalize both sides to lowercase letters and digits, and drop a `.install` / `.portable` suffix from the id. Ignore entries with `SystemComponent = 1`, entries with a `ParentKeyName` (updates), and entries without a positive DWORD `EstimatedSize` (KB).
  - Collapse entries with the same normalized `DisplayName`, since the same program shows up in HKLM, `WOW6432Node` and HKCU.
  - Then: an **exact** normalized match wins. Otherwise a **prefix** match counts only when the character after the prefix in the original `DisplayName` is a boundary (space, digit, `(`, `-`, `_`, `.`). So `git` matches "Git" (exact) and not "GitHub Desktop"; `vlc` matches "VLC media player". A package whose entry is named differently, such as `firefox` vs "Mozilla Firefox (x64 en-US)", gets no match and shows `–`: a missed estimate is accepted, a wrong one is not.
  - If more than one distinct entry remains after that, there is no estimate.
- **Companions:** the size is shown only on the base package's row (companions are hidden). The registry match uses the base id; `.install` / `.portable` stripping makes the companions' ids equal to it anyway.
- **Homebrew formula and group totals** count only what the listed package occupies, not its dependencies. The manual says so.
- **Sizes arrive per manager** as each one finishes, so npm's sizes don't wait for a big Cellar. With **Size** sort, unmeasured and unknown sizes sort last, and the list re-sorts as results arrive.

## Design

### Core

- **`PackageSize`:** a record `(long Bytes, bool IsEstimate)`. `null` means unknown.
- **`PackageKey`:** a record `(string ManagerId, string Name, PackageKind Kind)`, used as the dictionary key. `InstalledPackage` can't be the key, because its `Companions` list compares by reference.
- **`PackageManager`** gets `virtual Task<IReadOnlyDictionary<PackageKey, PackageSize?>> MeasureAsync(ICommandRunner runner, string executable, IReadOnlyList<InstalledPackage> packages, CancellationToken ct)`. The default returns all unknown.
  - Each manager implements it with the table above.
  - **Probe commands** run through `ICommandRunner` as probes with the existing 60 s timeout and each manager's environment: `brew --prefix`, `brew info --json=v2 --cask <tokens…>`, `npm root -g`, and `pkgutil --pkg-info` / `--files`.
    - JSON is cut from the first `{` to the last `}`, because stderr is mixed in.
    - If the one-call `brew info` fails (one stale cask fails the whole call), retry per token, and ignore the ones that still fail.
    - Casks are matched on `token`, `full_token` and `old_tokens`.
    - `pkgutil` is resolved with `runner.ResolveExecutable("pkgutil")` (it lives in `/usr/sbin`), only on macOS.
    - `pkgutil --files` paths are relative to the `volume` + `location` from `--pkg-info`, and the list includes directories. Each entry is checked: files are counted, directories are skipped (their files are listed separately), and symlinks are skipped.
  - **`DiskUsage`** gains a `CancellationToken` overload (checked in the enumeration loop), plus `MeasurePaths(IEnumerable<string>)`. It accepts files and directories, deduplicates by full path, and counts a directory nested inside another listed directory only once.
  - **The Windows registry read** sits behind a small interface, `IInstalledProgramsSource`, which returns `(DisplayName, EstimatedSizeKb, SystemComponent, ParentKeyName)` entries, so matching can be unit-tested.
    - The real implementation reads HKLM (64- and 32-bit views) and HKCU, under `[SupportedOSPlatform("windows")]`.
    - The Chocolatey root comes from `%ChocolateyInstall%`, or the parent of the resolved `choco` executable's `bin` folder. Only the registry is used for sizes, but the root is kept for future use; if it isn't needed, drop it.
- **Pure parsers and matchers** are `internal static`, so tests can reach them:
  - cask JSON to (app paths, delete paths, pkgutil ids);
  - `pkgutil --pkg-info` output to the volume and location prefix;
  - Chocolatey name normalization and matching.
- **`PackageInventory`** gets `Task MeasureAsync(IReadOnlyList<ManagerInventory> inventories, Action<IReadOnlyDictionary<PackageKey, PackageSize?>> onManagerMeasured, CancellationToken ct)`. It runs the managers one after another and reports each manager's results as they arrive. One manager's failure (an exception) yields unknown sizes for that manager only, written to `Trace`.

### Desktop

- **`InstalledViewModel`:** after a list loads, it starts measuring with its own `CancellationTokenSource`, separate from the load's. A reload, Refresh, an uninstall run or Dispose cancels it.
  - Results for a list that has since been rebuilt are discarded: each measurement carries a generation number.
  - `IsMeasuring` is separate from `IsLoading`, so measuring never disables Refresh or Uninstall.
  - Results are applied per manager on the UI thread.
- **`PackageItem.SizeText`:** `"1.2 GB"`, `"≈ 1.2 GB"` or `"–"`, using `DiskUsage.Format`. It is empty while measuring.
- **`PackageGroup.TotalText`:** the sum of known sizes in the group, prefixed with `≈` if any are estimates. It is empty while measuring.
- **Sort:** `SortBySize` (bool), applied within each group. Ticks, search and visibility are unchanged.
- **Layout:**
  - size column right-aligned, muted like the version;
  - the group header shows the total after the count;
  - a small "Sort: Name | Size" control next to Refresh;
  - `Measuring sizes…` shown beside the search box while busy.

### Docs and tests

- **Tests (Core):**
  - cask JSON parsing: app cask, pkg-only cask, mixed, wildcard skipped;
  - `pkgutil` info parsing;
  - Chocolatey normalization and matching: exact, prefix, ambiguous → none, `.install` suffix;
  - per-manager `MeasureAsync` with `FakeRunner`, temp folders and a fake `IInstalledProgramsSource`;
  - estimate flags;
  - companion inclusion;
  - one manager's failure doesn't affect the others.
- **Manual:** a paragraph in the Installed section explaining sizes, `≈` and `–`. Regenerate the screenshots, with sample sizes in `tools/Screenshots`.
- **`docs/architecture.md`:** describe size measurement.

## Left to the builder

- How the cask JSON is requested: one call for all casks, or in chunks if the argument list gets long.
- Exact control styling, following the existing tab.
- Whether `pkgutil` file sizes are read with `stat` per file or summed in .NET from the listed paths. Summing in .NET is preferred: build the full paths from the volume and location prefix, and skip missing files.
