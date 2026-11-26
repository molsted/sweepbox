# Architecture

Sweepbox is two projects plus tests:

```
Sweepbox.Desktop  (Avalonia UI, CLI entry point, updater)   ──references──▶   Sweepbox.Core  (no UI)
Sweepbox.Tests    (xUnit)                                   ──references──▶   Sweepbox.Core
```

`Sweepbox.Core` holds all behaviour that can be tested without a UI. `Sweepbox.Desktop` wires Core together, shows it in the tray/menu bar and windows, and handles self-updates. Keep new logic in Core.

## Sweepbox.Core

| Folder | Types | Responsibility |
|---|---|---|
| `Managers/` | `PackageManager` (abstract), `BrewManager`, `ChocoManager`, `NpmManager`, `PackageInventory`, `InstalledPackage`, `UninstallNotes` | Turn settings into an ordered list of `CommandSpec` steps. Say which folders cleanup steps shrink. List installed packages and build uninstall commands |
| `Running/` | `CommandSpec`, `CommandResult`, `ICommandRunner`, `ProcessCommandRunner`, `ShellEnvironment`, `DiskUsage` | Find tools on the user's PATH, run one command with a timeout, measure folder sizes |
| `Engine/` | `MaintenanceEngine`, `Scheduler`, `RunReport`, `RunHistoryStore` | Run the managers and uninstalls, record the results, decide when the next run is due |
| `Settings/` | `AppSettings`, `SettingsStore`, `AppPaths`, `JsonFile` | User settings, data folder locations, atomic JSON persistence |
| `Platform/` | `LaunchAtLogin` | LaunchAgent plist (macOS) / `HKCU\…\Run` value (Windows) |

### Package managers

Each manager is a `PackageManager` subclass in `Managers/PackageManager.cs`:

- `Id` (`brew`, `choco`, `npm`) is the stable key used in run reports and "Run Only".
- `ExecutableName` is looked up on the resolved PATH. If it isn't found, the manager is reported as *Skipped*.
- `IsSupportedOnThisOS` filters `PackageManager.ForThisOS`. Homebrew is macOS/Linux, Chocolatey is Windows, npm is everywhere.
- `BuildSteps(settings, executable)` is pure: it returns the commands for one run and has no side effects. That is why `ManagerStepTests` can check the exact argument lists.
- `ListInstalledAsync` runs probes and parses them with pure `internal static` functions. Homebrew uses `list --cask --versions`, `leaves --installed-on-request` and `list --formula --versions` (dependency-only formulae are deliberately not listed). Chocolatey checks `--version` first and needs 2.x, because on 1.x `list` queries the remote feed, then runs `list --limit-output` (`name|version`). npm runs `ls --global --depth=0 --json` and trusts the JSON over the exit code. Chocolatey's `X.install`/`X.portable` packages are hidden when `X` is listed and become `X`'s `Companions`. Homebrew lines whose first word isn't a package name (merged-in warnings and errors) are skipped. A failing listing becomes a `ManagerInventory` with an error naming the command and its exit code and last output line (or the timeout), not an exception; if Homebrew's casks list but its formulae don't, the casks come back with the error.
- `BuildUninstallStep` returns the uninstall command and `IsProtected` hides packages that must never be offered (`npm`, `corepack`, `chocolatey`, `chocolatey-*.extension`). No uninstall command uses `--force`. Homebrew sets `HOMEBREW_NO_AUTOREMOVE=1` when the Autoremove setting is off, and Chocolatey elevates when `ElevateWithUac` is on.
- `GetCleanupLocationsAsync` may run quick *probe* commands (for example `brew --cache`, via `QueryPathAsync`) to find the folders whose size is measured.

`CommandSpec` flags:

- `IsCleanup`: the step frees disk space. Freed space is measured around these steps.
- `Elevate`: Windows only. Run with administrator rights.
- `IsProbe`: a lookup, not a maintenance step.
- `Environment`: per-step environment variables (for example `HOMEBREW_NO_AUTO_UPDATE=1`).

### Running commands

`ShellEnvironment.ResolvePathAsync` builds the PATH once per run:

- **macOS/Linux:** the settings' `ExtraPath`, then the PATH printed by the user's login shell (`$SHELL -ilc`, so `.zprofile` and `.zshrc` are both read: Homebrew, nvm, volta, fnm), then the process PATH, then fixed fallbacks (`/opt/homebrew/bin`, `/usr/local/bin`, …). Apps started from Finder get only a minimal PATH, which is why the login shell is asked.
- **Windows:** `ExtraPath`, then the machine and user PATH read fresh from the registry (so tools installed after the app started are found), then the process PATH.

`ProcessCommandRunner` runs one `CommandSpec`:

- stdin is redirected and closed immediately. Anything that prompts (sudo, a confirmation) fails fast instead of hanging.
- stdout and stderr are streamed line by line. Lines are serialized under a lock, so callbacks needn't be thread-safe.
- A timeout (`StepTimeoutMinutes`) and the run's cancellation token both kill the whole process tree.
- **Elevated Windows steps:** a UAC-elevated process can't have its output redirected. The runner writes a temporary `.cmd` that redirects output to a log file, starts it with `Verb = "runas"`, then replays the log. A declined UAC prompt (Win32 error 1223) becomes a readable error.

### A maintenance run

`MaintenanceEngine.RunAsync(trigger, onlyManagerIds)`:

1. Returns `null` straight away if a run is already in progress (`SemaphoreSlim` gate, no waiting).
2. Opens `logs/run-yyyyMMdd-HHmmss.log` and prunes the folder to the newest 60 logs.
3. Resolves the PATH through the runner factory and picks the managers: enabled and supported, or exactly `onlyManagerIds` (which runs even disabled managers).
4. For each manager, in order:
   1. Resolve the executable.
   2. Build the steps.
   3. Run them one by one.
   4. Measure the cleanup folders just before the first `IsCleanup` step and just after the last one. Upgrades add new versions, so measuring around the whole run would distort the number.
5. Records a `RunReport` (with per-manager `ManagerReport` and per-step `StepReport`) in `RunHistoryStore`, even when an unexpected exception ends the run.

A failed step doesn't stop the manager's later steps. The manager's outcome is *Failed* if any step failed. Cancellation marks the current and all remaining managers *Cancelled*.

Events: `StatusChanged` (current manager/step), `Output` (each timestamped log line) and `RunCompleted`. **They are raised on background threads.** Desktop code must marshal to the UI thread (`Dispatcher.UIThread.Post`) before touching UI state.

### Listing and uninstalling

`PackageInventory.ListAsync` lists every supported manager whose executable is found, whether or not it is enabled, one after another. It takes no run gate, because it only reads. `UninstallNotes` builds the confirmation notes, such as the Homebrew dependency clean-up and the UAC prompts.

`MaintenanceEngine.UninstallAsync(packages)` shares the gate, log file, events, cancellation and history recording with `RunAsync`, and records the run with `RunTrigger.Uninstall`. Each manager gets one `ManagerReport` with one `StepReport` per package; a package's companions (Chocolatey's hidden `X.install`/`X.portable`) follow it as their own steps, so uninstalling `git` also removes `git.install`. Protected names are filtered after this expansion. An optional `onStarted` callback runs once the run is accepted, before its first log line; the Installed tab clears the live log there, so a rejected uninstall never clears another run's log. A failed package doesn't stop the next one. There is no cleanup measurement, so uninstalls never add to space freed. It returns `null` if a run is already in progress.

Uninstalls must not look like maintenance runs. `RunHistoryStore.LatestMaintenance` is the newest run whose trigger isn't `Uninstall`. `Scheduler.NextRunAt` and `EngineStatus.LastRun` use it, so an uninstall changes neither the schedule nor the Overview tab and tray's "Last run". The history list shows all runs.

### Scheduling

`Scheduler` checks once a minute whether a run is due. It doesn't set a single long timer. Polling means sleep/wake and clock changes need no special handling: an overdue run starts within a minute of waking.

`Scheduler.ComputeNextRun` is the rule: the next run is the last run's start time plus the interval, but never earlier than app start plus the start delay. With no history, it is app start plus the start delay. The schedule is off when `ScheduleEnabled` is false or the user paused it (the pause isn't persisted and resets when the app restarts).

Both `Scheduler` and `MaintenanceEngine` take a `TimeProvider`. Tests drive them with `FakeTimeProvider`.

### Settings and data files

`AppPaths.Default()` picks the data folder:

- macOS: `~/Library/Application Support/Sweepbox`
- Windows: `%APPDATA%\Sweepbox`
- Linux: `$XDG_CONFIG_HOME` or `~/.config`

On first start it moves the folder over from the app's former name, `PkgCleaner`. The folder holds `settings.json`, `history.json` (the last 50 runs), `logs/` and the `.lock` file.

`SettingsStore`:

- `Current` returns a **copy**. Change the copy and pass it to `Save`. Never hold on to it expecting live updates.
- `Save` normalizes the values (`AppSettings.Normalize` clamps ranges and replaces nulls), writes atomically, and raises `Changed`.
- `JsonFile` writes to a temp file and then moves it into place, so a crash never leaves half a file. A corrupt file is copied to `*.corrupt` and the defaults are used.

JSON uses camelCase and string enums. Missing properties get the C# defaults, so a new setting needs a safe default. Old settings files don't have it.

## Sweepbox.Desktop

| Area | Types | Notes |
|---|---|---|
| Entry point | `Program` | Parses `--dry-run`, `--run` and `--help`. Otherwise holds the single-instance lock and starts Avalonia |
| Composition | `AppServices` | Creates `SettingsStore`, `RunHistoryStore`, `MaintenanceEngine`, `PackageInventory` and `Scheduler`, and exposes `Inventory` and `UninstallAsync`. Sends notifications and keeps the login item up to date |
| App lifetime | `App` | Creates the services, tray and updater. Opens the status and settings windows (one of each). Handles first run |
| Tray / menu bar | `Tray/TrayController` | Icon state (idle/running/error), the menu, Run Now / Run Only / Cancel / Pause |
| Windows | `Views/`, `ViewModels/` | MVVM with CommunityToolkit.Mvvm. `SettingsViewModel` edits a working copy of `AppSettings`, and `StatusViewModel` shows history and the live log. Its tabs are Overview, Installed and Live log. `InstalledViewModel` loads the groups on first show, keeps ticks across searches and refreshes after each run |
| Updates | `Services/AppUpdater`, `Services/SweepboxSparkleUpdater`, `Services/SweepboxUIFactory`, `Updates/` | NetSparkle self-update with Sweepbox-styled dialogs |
| Helpers | `Services/Notifier`, `Services/Format` | Native notifications, text formatting |

### Process model

- **Single instance:** the process holds an exclusive `FileStream` on `.lock` for its whole lifetime. A second instance retries for about 5 seconds before it gives up, because after an in-place update the new process can start while the old one is still exiting. `--run` takes the same lock. `--dry-run` doesn't.
- **No Dock icon on macOS** (`ShowInDock = false`). The app runs until Quit (`ShutdownMode.OnExplicitShutdown`).
- **First run:** if `settings.json` doesn't exist, the defaults are saved and the settings window opens. Installed copies (not dev builds in `bin/`) also ask about start at login.
- **CLI on Windows:** the exe is a GUI subsystem app. CLI modes call `AttachConsole(-1)` so the output reaches the calling terminal.

### Self-update

`AppUpdater` uses NetSparkle against a signed appcast for each OS and architecture: `releases/latest/download/appcast-{macos|windows}-{arm64|x64}.xml`. The appcast signature is checked with the Ed25519 public key embedded from `installer/sparkle-public-key.txt`. Automatic checks follow the `SelfUpdate` setting, and the user can always check manually. The dialog windows in `Updates/` are created by `SweepboxUIFactory`. They have no parameterless constructor on purpose, which is why `AVLN3001` is suppressed. See [releasing.md](releasing.md) for how the appcasts are made.

## Tools outside the app

| Tool | What it does |
|---|---|
| `tools/Screenshots` | References Desktop and renders the real windows with sample data, headless (Avalonia headless + Skia), into `user-docs/images/raw` |
| `tools/frame_screenshots.py` | Adds window chrome to those renders and draws the menu illustrations in `user-docs/images` |
| `tools/generate_icons.py` | Draws the app icon, tray icons, DMG background and installer bitmaps |
| `tools/generate_site_graphics.py` | Draws the website hero, social card and favicons |
| `tools/build_site.py` | Builds `_site/` from `site/`, `user-docs/MANUAL.md` and `LICENSE` |
