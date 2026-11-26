# AGENTS.md

Instructions for AI coding agents working in this repository.

Sweepbox is a .NET 10 / Avalonia 12 menu bar (macOS) and system tray (Windows) app. It runs Homebrew, Chocolatey and npm maintenance on a schedule and reports how much disk space the clean-up freed.

## Where things are documented

| Folder | Audience | Contents |
|---|---|---|
| [`docs/`](docs/README.md) | Developers and agents | [architecture](docs/architecture.md), [development](docs/development.md), [releasing](docs/releasing.md) |
| [`user-docs/`](user-docs/MANUAL.md) | End users | The manual and its screenshots, published on the website as `docs.html` |
| [`README.md`](README.md) | Everyone | Overview, install and a quick start |

Read [docs/architecture.md](docs/architecture.md) before changing how runs, scheduling, settings or updates work. If your change makes something in `docs/` or `user-docs/` wrong, update that document in the same change.

## Layout

```
src/Sweepbox.Core       All logic, no UI: managers, command runner, engine, scheduler, settings
src/Sweepbox.Desktop    Avalonia UI, CLI entry point (Program.cs), NetSparkle self-update
tests/Sweepbox.Tests    xUnit tests for Core
tools/                  Screenshot renderer, icon/site graphics generators, website builder
site/                   GitHub Pages website
installer/, scripts/    macOS DMG and Windows Inno Setup packaging, clean scripts
```

## Commands

```bash
dotnet build Sweepbox.slnx
dotnet test Sweepbox.slnx                                          # must pass before you finish
dotnet test Sweepbox.slnx -c Release -p:RestoreLockedMode=true     # what CI runs
dotnet run --project src/Sweepbox.Desktop -f net10.0 -- --dry-run  # show the commands a run would execute
```

**Don't** run `--run`, Run Now or the scheduled app unless the user asks you to. They really upgrade and clean the machine's package managers.

## Rules

- Put logic in `Sweepbox.Core` and keep `Sweepbox.Desktop` a thin UI layer. Core has `TreatWarningsAsErrors`: fix warnings, don't suppress them.
- Core gets time from an injected `TimeProvider` and starts processes only through `ICommandRunner`. `PackageManager.BuildSteps` stays pure.
- Engine and scheduler events fire on background threads. Marshal them to the UI thread with `Dispatcher.UIThread.Post`.
- Commands that Sweepbox runs must never wait for input: stdin is closed and every step has a timeout.
- A new setting needs a safe default, because existing `settings.json` files won't have it. A new nested settings class must be added to `AppSettings.Clone()` and `Normalize()`.
- Write tests as behaviour sentences in `Snake_case`. They use temp directories and must never start a real package manager.
- After changing a `PackageReference`, run `dotnet restore` and commit the updated `packages.lock.json` files. Upgrade the .NET SDK pin (`10.0.301` in `build.yml`), the `Microsoft.NET.ILLink.Tasks` reference and the lock files together.
- If you change user-visible behaviour, update `README.md` and `user-docs/MANUAL.md`. If a window looks different, regenerate the screenshots (see [docs/development.md](docs/development.md#change-a-window)).

## Never

- Generate, print or commit a Sparkle private key, or change `installer/sparkle-public-key.txt`, unless the user asks you to. See [docs/releasing.md](docs/releasing.md#the-in-app-updater-and-its-keys).
- Widen workflow `permissions`, or use a third-party action that isn't pinned to a commit SHA.
- Tag, release or bump `<Version>` in `Directory.Build.props` unless the user asks you to.
- Commit build output (`bin/`, `obj/`, `artifacts/`, `_site/`, `user-docs/images/raw/`) or IDE files.

## Git

Work on a branch (`fix/…`, `feat/…`, `chore/…`, `security/…`) and open a PR against `main`. Write commit subjects in the imperative and in sentence case. For example: "Pin the CI SDK to the version the lock files were generated with".
