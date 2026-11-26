# Development

## Requirements

- **.NET 10 SDK.** CI uses exactly `10.0.301` (see [Dependencies](#dependencies-and-lock-files)).
- **Optional:** Python 3 with Pillow, to regenerate icons, screenshots or site graphics. Run `pip install -r tools/requirements-site.txt` to build the website.
- **Optional:** Xcode 26+ on macOS (for the macOS 26 app icon) or Inno Setup 6 on Windows, to build installers. See [releasing.md](releasing.md).

## Build, run, test

```bash
dotnet build Sweepbox.slnx
dotnet test Sweepbox.slnx                                         # unit tests (Core only)
dotnet test Sweepbox.slnx -c Release -p:RestoreLockedMode=true    # exactly what CI runs

dotnet run --project src/Sweepbox.Desktop -f net10.0                   # the tray / menu bar app
dotnet run --project src/Sweepbox.Desktop -f net10.0 -- --dry-run      # print the commands a run would execute, then exit
dotnet run --project src/Sweepbox.Desktop -f net10.0 -- --run          # one real headless run
```

`-f net10.0` runs the cross-platform build; on Windows use `-f net10.0-windows10.0.19041.0` instead (the Windows target is what the installer ships).

> ⚠️ `--run`, **Run Now** and the scheduler really upgrade and clean the Homebrew/Chocolatey/npm installs on your machine. Use `--dry-run` to check what changed in the steps. To test the run flow safely, turn managers off in Settings or point `ExtraPath` at a folder of stub scripts.

A dev build uses the **same data folder** as an installed copy: `~/Library/Application Support/Sweepbox` on macOS, `%APPDATA%\Sweepbox` on Windows. It shares the same single-instance lock too, so quit the installed app before running a dev build. Delete `settings.json` to see the first-run flow again (the start-at-login question appears only in installed copies).

Logs from the app itself go to `Trace`. Each run's output is in `logs/run-*.log` in the data folder, and the Status window has links to them.

## Project rules

- **`Sweepbox.Core` has `TreatWarningsAsErrors`.** Fix warnings rather than suppressing them.
- C# `latest`, nullable and implicit usings on. Use file-scoped namespaces that match the folder (`Sweepbox.Core.Managers`), `sealed` classes, records for data, and collection expressions (`[]`).
- Doc comments (`/// <summary>`) on public members whose purpose isn't obvious from the name. Comments explain *why*.
- Core logic must stay testable:
  - Time comes from an injected `TimeProvider`. Don't use `DateTime.Now` or `DateTimeOffset.Now`.
  - Processes start only through `ICommandRunner`.
  - `PackageManager.BuildSteps` stays pure.
- Engine and scheduler events arrive on background threads. In Desktop, marshal them with `Dispatcher.UIThread.Post` (see `StatusViewModel`).
- Avalonia uses compiled bindings by default: give every view `x:DataType`.
- Commands that Sweepbox runs must never wait for input. If a tool has a "yes" flag (`--yes`, `-y`) or a non-interactive environment variable, set it.
- Guard OS-specific code with `OperatingSystem.IsMacOS()` / `IsWindows()`. CI builds and tests on macOS and packages on Windows, so Windows-only code is compiled but not exercised by the tests.

## Tests

`tests/Sweepbox.Tests` (xUnit 2) covers Core. `InternalsVisibleTo` gives it access to `internal` members.

| File | Covers |
|---|---|
| `ManagerStepTests.cs` | Exact argument lists from `BuildSteps` for each setting combination |
| `EngineTests.cs` | `MaintenanceEngine` and `Scheduler` with a fake `ICommandRunner` and `FakeTimeProvider`: outcomes, cancellation, freed space, scheduling |
| `InfrastructureTests.cs` | Settings and history persistence, `AppPaths` migration, PATH parsing and lookup, `ProcessCommandRunner` against real short-lived shell commands (output, environment, closed stdin, timeout), app bundle detection, disk usage and size formatting |

Test methods are behaviour sentences in `Snake_case` (`Brew_greedy_and_prune_all_add_flags`). Use temp directories for anything that touches disk, and never the real data folder. The tests must not start a real package manager.

There are no automated UI tests. Check UI changes by running the app. `tools/Screenshots` renders every window headless, which also shows layout problems quickly.

## Common changes

### Add or change a step of a package manager

1. Change `BuildSteps` (and `GetCleanupLocationsAsync` if the step frees space) in `src/Sweepbox.Core/Managers/PackageManager.cs`. Set `IsCleanup` on steps that free space and `Elevate` on Chocolatey steps that need admin rights.
2. If it needs a new option, add it to the manager's settings class in `Settings/AppSettings.cs` with a safe default. Older `settings.json` files don't contain it. If it needs clamping or a null check, extend `Normalize()`.
3. Show it in `ViewModels/SettingsViewModel.cs` and `Views/SettingsWindow.axaml`.
4. Add or extend a test in `ManagerStepTests.cs`.
5. Update the step table in `README.md` and the manager's section in `user-docs/MANUAL.md`.

### Add a package manager

1. Subclass `PackageManager`, add it to `PackageManager.All`, and give it a `ManagerSettings` subclass.
2. Add that settings class to `AppSettings`, to `Clone()` (otherwise two settings copies share it) and to `Normalize()`.
3. Add a section to the settings window, the tray's Run Only menu (it lists `Engine.Managers`), and tests.
4. Add the manager to the README table, the manual, and the website text in `site/index.html`.

### Add an app setting

Add a property with a default to `AppSettings`. If it has a valid range, clamp it in `Normalize()`. Then bind it in the settings window. Settings are saved as camelCase JSON.

### Change a window

1. Edit `Views/*.axaml` and its view model.
2. Look at it in light and dark mode on both platforms if you can.
3. Regenerate the manual screenshots if the window looks different:

   ```bash
   dotnet run --project tools/Screenshots -- user-docs/images/raw && python3 tools/frame_screenshots.py
   ```

   This writes `user-docs/images/*-{macos,windows}[-dark].png`. `user-docs/images/raw/` is gitignored.

### Change the icons or graphics

`python3 tools/generate_icons.py` redraws the app icon, tray icons, DMG background and installer bitmaps. `python3 tools/generate_site_graphics.py` redraws the website images. Commit the generated files.

## Dependencies and lock files

Every project has a committed `packages.lock.json`, and CI restores with `RestoreLockedMode=true`, so the full NuGet graph is pinned.

- After changing a `PackageReference`, run `dotnet restore` and commit the updated lock files with the change. Otherwise CI fails.
- `Sweepbox.Desktop` restores for exactly the runtime identifiers `osx-arm64;osx-x64;win-x64;win-arm64`. The lock file covers all four.
- The SDK picks the version of `Microsoft.NET.ILLink.Tasks` from its own patch level. That is why it is referenced explicitly in `Sweepbox.Desktop.csproj`, and why every job in `.github/workflows/build.yml` pins `dotnet-version: 10.0.301`. **Upgrade the SDK pin, that reference and the lock files together.**
- The NetSparkle packages are previews that match Avalonia 12. Upgrade the Avalonia packages together, and NetSparkle as a matched pair.
- Dependabot opens update PRs (`.github/dependabot.yml`). Check that their lock-file changes build.
- The website's Python dependencies in `tools/requirements-site.txt` are installed with `--require-hashes`. Update a version and its hashes together.

## Cleaning up

`scripts/clean.command` (macOS/Linux) and `scripts/clean.ps1` (Windows) remove `bin/`, `obj/`, `artifacts/`, `_site/`, `user-docs/images/raw/`, `TestResults/` and Python caches. They list what will be removed and ask first. `--dry-run` / `-DryRun` only lists, and `--yes` / `-Yes` doesn't ask.
