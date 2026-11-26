# Design: See what's installed and uninstall what you no longer need

Date: 2026-11-26 · Status: draft for approval

## Intent

The user wants to remove apps and tools they no longer need, from inside Sweepbox, without going to a terminal. Today Sweepbox only upgrades and cleans. It never shows what is installed, and it can't remove anything.

Success: open Sweepbox, see a list of what you installed, tick a few things, confirm, and they're gone. The result shows in the log and history like any other run.

## Goals

- An **Installed** tab in the Status window lists what each supported, installed package manager has installed:
  - **Homebrew** (macOS): casks, plus formulae you installed yourself.
  - **Chocolatey** (Windows): local packages.
  - **npm** (both): global packages.
- A search box filters the list by name. The user ticks packages across managers, presses **Uninstall selected (n)**, and confirms a dialog that names every package.
- The uninstall runs through `MaintenanceEngine`. It never overlaps a maintenance run, its output streams into **Live log**, and it is recorded in history and in a `logs/run-*.log` like any other run.
- Failures, for example a cask uninstaller that wants a password, are reported per package and don't stop the other packages.

## Non-goals

- Installing packages, upgrading single packages, or showing available updates.
- Package sizes, last-used dates, descriptions or homepages. The user only asked to remove things they no longer need. Showing the installed version is enough.
- Measuring the disk space an uninstall frees. Uninstall runs don't contribute to "space freed".
- Uninstalling from the tray menu or the command line.
- Apps not managed by a package manager (for example drag-installed `.app`s or the Windows "Apps & features" list).
- A Linux UI.

## Anti-goals (failure even if it works)

- **Breaking something the user didn't ask to remove.** That means no `--force`, no `--zap`, no `--ignore-dependencies`, and no removal of dependencies the user never chose.
- **Uninstalling without an explicit confirmation** that lists exactly what will go.
- **Letting the user remove a package manager with itself:** `npm` from npm, `chocolatey` from Chocolatey.
- **Hiding the result:** an uninstall that silently fails, or that disappears from the log or history.
- **Disturbing the maintenance schedule or the Overview tab.** An uninstall run must not count as "the last run" for scheduling, and must not replace the last maintenance result on the Overview tab or in the tray menu.

## Constraints from the existing code

- Commands run with stdin closed and a timeout (`ProcessCommandRunner`). Every confirmation must happen in Sweepbox before the command starts. An uninstaller that prompts fails and is reported, which is acceptable.
- `MaintenanceEngine` allows one run at a time. It returns `null` immediately if a run is in progress.
- `Scheduler.NextRunAt` uses `RunHistoryStore.Latest.StartedAt`, and `EngineStatus.LastRun` drives the Overview tab and the tray's "Last run" line. Both currently take the latest run of any kind, which conflicts with the last anti-goal.
- Chocolatey commands need admin rights. The existing `ChocoSettings.ElevateWithUac` setting and `CommandSpec.Elevate` handle this (one UAC prompt per command).
- `TreatWarningsAsErrors` in Core, and the rules in `AGENTS.md`: logic in Core, testable through `ICommandRunner`.

## Approaches considered

| | Approach | Trade-off |
|---|---|---|
| **A (chosen)** | "Installed" tab in the existing Status window | The Status window is already opened from the tray. Clicking Uninstall can switch to the Live log tab, so the output shows without new plumbing. No new tray items. Slightly less discoverable than its own menu item |
| B | Separate "Installed Apps…" window opened from the tray | Easier to find, but it's one more window to keep in sync with runs in progress, and the user has to switch windows to see the output |
| C | "Uninstall ▸" submenus in the tray menu | No new window, but no search or multi-select. It becomes unusable with 100+ packages |

The user picked **A**.

## Design

### Core: listing installed packages

New record in `Sweepbox.Core.Managers`:

```csharp
public sealed record InstalledPackage(string ManagerId, string Name, string? Version, string Kind);
// Kind: "Cask", "Formula", "Package". Used for grouping and for building the uninstall command.
```

New virtual methods on `PackageManager`:

- `Task<IReadOnlyList<InstalledPackage>> ListInstalledAsync(ICommandRunner runner, string executable, CancellationToken ct)`
- `CommandSpec BuildUninstallStep(AppSettings settings, string executable, InstalledPackage package)`
- `bool IsProtected(InstalledPackage package)`: true for packages that must never be offered for removal.

Listing runs the commands as probes (`IsProbe = true`, 60 s timeout, same environment variables as the manager's steps). It collects stdout lines and parses them with **pure, `internal static` parse functions** so tests can feed sample output.

| Manager | Listing commands | Uninstall command | Protected |
|---|---|---|---|
| Homebrew | `brew list --cask --versions` (lines: `name version…`). `brew leaves --installed-on-request` for the formula names. `brew list --formula --versions` to join in versions | `brew uninstall --cask <name>` / `brew uninstall --formula <name>` | none |
| Chocolatey | `choco list --limit-output` (lines: `name\|version`, Chocolatey v2 = local only) | `choco uninstall <name> --yes --no-progress --limit-output`, `Elevate = settings.Choco.ElevateWithUac` | `chocolatey` |
| npm | `npm ls --global --depth=0 --json` (`dependencies` object → name/version) | `npm uninstall --global <name>` | `npm`, `corepack` |

Homebrew formulae installed only as dependencies are deliberately not listed. Removing them by hand is what breaks things, and the existing `autoremove` step already clears out dependencies nothing needs any more. Homebrew's own uninstall refuses to remove a formula another formula still needs. That refusal is reported as an ordinary failure.

**Homebrew's automatic dependency clean-up** (my call): since Homebrew 4.3, `brew uninstall` also removes formulae that nothing needs any more (its built-in autoremove). The uninstall step follows the user's existing **Autoremove** setting:
- Off: the step sets `HOMEBREW_NO_AUTOREMOVE=1`.
- On: Homebrew does what a maintenance run would do anyway.

When the selection contains Homebrew items and Autoremove is on, the confirmation dialog says "Homebrew will also remove dependencies that nothing else uses."

**Chocolatey dependency packages** (my call): `choco list` returns every local package, including dependencies.
- `X.install` and `X.portable` are hidden when the meta-package `X` is also installed, because uninstalling `X` is the user's real choice.
- `chocolatey` and `chocolatey-*.extension` are protected.
- Other dependency-like packages (`vcredist*`, `KB*`, `dotnetfx`) stay visible, because users sometimes install them directly. Without `--force`, Chocolatey refuses to uninstall a package that another one depends on, and that refusal is reported as an ordinary failure. The uninstall command never passes `--force` or `--force-dependencies`.

**Chocolatey version** (my call): only Chocolatey 2.x is supported, where `choco list` means local packages. Listing first runs `choco --version`. On 1.x the Chocolatey group shows "Chocolatey 2 or later is needed to list packages" and offers nothing, so it never presents the remote feed as installed packages.

A listing command that fails (non-zero exit, or output that can't be parsed) yields an error for that manager, not an exception. The UI shows "Couldn't list Homebrew packages – see log" for that group, and the other groups still show.

### Core: running an uninstall

- Add `RunTrigger.Uninstall`.
- Add a method to the engine:

  ```csharp
  Task<RunReport?> UninstallAsync(IReadOnlyList<InstalledPackage> packages)
  ```

  It uses the same gate, log file, `Output`/`StatusChanged` events, cancellation and history recording as `RunAsync`. Each affected manager gets one `ManagerReport`, with one `StepReport` per package ("Uninstall zoom"). It has no cleanup measurement. A failed package doesn't stop the next one. It returns `null` if a run is already in progress.
- The UI calls the engine method through `AppServices`, which wires the engine for the Desktop app.
- Refactor `RunAsync` so the shared parts (gate, log, status, recording) are one private method that takes "which managers, and how to get their steps". This avoids a second copy of the run loop. The exact shape is left to the builder.
- Add listing access for the UI: `Task<InstalledListing> ListInstalledAsync(CancellationToken ct)` on the engine (or a small `InstalledPackages` service in Core). It resolves the PATH the same way a run does, and returns the packages per manager plus a per-manager error message. Listing doesn't take the run gate, because it is read-only, but it is disabled in the UI while a run is in progress (see UI).

### Core: keep uninstalls out of the schedule and the Overview

- `RunHistoryStore` gets `LatestMaintenance`: the newest run whose `Trigger` is not `Uninstall`.
- `Scheduler.NextRunAt` uses `LatestMaintenance`.
- `EngineStatus.LastRun` and its initial value use the latest maintenance run. After an uninstall run, `LastRun` stays the previous maintenance run. The Overview tab and the tray's "Last run" line therefore keep showing maintenance.
- The Status window's **history list** shows all runs, uninstalls included. The trigger column says "Uninstall". Total freed is unchanged, because uninstall reports have `BytesFreed = null`.
- Failure notifications (`AppServices.Notify`) apply to uninstall runs too: "Uninstall failed" / "Uninstall finished", with the same settings.

### Desktop: the Installed tab

The tab sits between Overview and Live log:

```
[ Overview ] [ Installed ] [ Live log ]
 Search: [____________]                         [ Refresh ]
 ▾ Homebrew – apps (casks)                       12
   ☐ google-chrome        131.0.6778.86
   ☑ zoom                 6.2.11
 ▾ Homebrew – command-line tools                 34
   ☐ ffmpeg               7.1
 ▾ npm (global)                                   5
   ☑ typescript           5.6.3
                               [ Uninstall selected (2) ]
```

- A new `InstalledViewModel` (owned by `StatusViewModel` or set as the tab's `DataContext`) holds the groups, the search text, the selection and the commands.
- **Loading:** the list loads the first time the tab is shown in a window's lifetime, and again on Refresh and after every uninstall run completes. While loading, it shows a spinner or "Loading…". Groups list only managers that are supported on this OS and whose executable was found. A manager that is disabled in Settings is still listed, because uninstalling doesn't depend on maintenance settings.
- **Group labels:** "Homebrew – apps (casks)", "Homebrew – command-line tools", "Chocolatey", "npm (global)". Each group shows its count, and names are sorted case-insensitively. Protected packages are not shown at all.
- **Search** filters by case-insensitive substring of the name, across all groups. Ticked items stay ticked while hidden by the filter. The button count includes hidden ticked items, and the confirmation lists every item.
- **Uninstall selected (n)** is disabled when nothing is ticked or a run is in progress. Clicking it opens a modal confirmation dialog:
  - It lists every package as "name (manager)".
  - On Windows with Chocolatey items and `ElevateWithUac` on, it notes that there will be one administrator prompt per Chocolatey package.
  - Buttons: **Uninstall** (not the default button) and **Cancel** (default, and Esc).
- **After confirming:** the window switches to the **Live log** tab, and the engine runs the uninstall. When it completes, the list refreshes and the ticks clear. Packages that failed are still installed, so they reappear in the refreshed list. The log and history show why they failed.
- While any run is in progress (scheduled, manual or uninstall), Refresh and Uninstall are disabled and the tab shows "Sweepbox is busy – the list will refresh when it finishes".
- It works in light and dark mode on both platforms, with compiled bindings (`x:DataType`).

### Docs and tests

- **Tests** (Core, no real package manager):
  - The parsers for each manager's sample outputs: normal, empty, junk lines, a cask with several versions on one line, npm JSON without `dependencies`.
  - `BuildUninstallStep` arguments for each manager and kind, including Chocolatey elevation following the setting.
  - The protected filters.
  - The engine's uninstall through `FakeRunner`: per-package reports, continues after a failure, cancellation, returns `null` when busy, recorded with `Trigger = Uninstall`.
  - `LatestMaintenance`, and that `Scheduler.NextRunAt` ignores uninstall runs.
- **Docs:**
  - `user-docs/MANUAL.md`: a section on the Installed tab, and new screenshots via `tools/Screenshots` (add the tab to the renderer with sample data).
  - `README.md`: the feature list.
  - `docs/architecture.md`: listing, uninstall runs and `LatestMaintenance`.

## Left to the builder

- The exact refactor shape inside `MaintenanceEngine`, and whether listing lives on the engine or in a small separate service.
- The tree control vs. grouped `ItemsControl`s with expanders, the spinner styling, and the exact dialog layout. Follow the existing windows' style.
- The exact `brew` and `choco` output-parsing details, as long as tests cover the sample formats above.
- Commit and PR breakdown.
