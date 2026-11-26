# Native Notifications Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the `osascript` (macOS) and PowerShell (Windows) notification helpers with in-process native notifications that show as "Sweepbox" with its icon.

**Architecture:**
- **macOS:** `UNUserNotificationCenter` through Objective-C runtime P/Invoke, using a hand-built block and a runtime-created delegate class.
- **Windows:** the Desktop project gains a Windows-specific target framework that exposes the WinRT toast API, plus a per-user AppUserModelID registry entry.
- **`Notifier.Show`** keeps its signature and dispatches to the two new classes.

**Tech Stack:** .NET 10 (SDK 10.0.301), Avalonia 12.2, Objective-C runtime (`libobjc`, UserNotifications.framework), Windows SDK projection (`net10.0-windows10.0.19041.0`), Inno Setup 6 Pascal script.

**Spec:** `docs/superpowers/specs/2026-11-26-native-notifications-design.md` (read it in full; its "Spike result" section is binding evidence).

## Global Constraints

- **Ids and targets:**
  - AUMID and bundle id: `dk.sweepbox.desktop`.
  - Windows target framework: `net10.0-windows10.0.19041.0`, with `SupportedOSPlatformVersion` `10.0.17763.0`.
  - Desktop `TargetFrameworks`: `net10.0;net10.0-windows10.0.19041.0` on every OS, with `EnableWindowsTargeting` `true`.
- **Registry:** `HKCU\Software\Classes\AppUserModelId\dk.sweepbox.desktop` gets `DisplayName` = `Sweepbox` and `IconUri` = the full path of `app-icon.png` next to the executable.
- **No fallbacks:** never fall back to `osascript` or PowerShell. Notification failures only write to `Trace`; nothing throws to callers, and nothing is shown to the user.
- **Objective-C exceptions abort the process.** Guard every `objc_msgSend` input: non-null class, selector and receiver, and never a nil `NSString` (substitute `""`).
- **macOS permission:** requested once at GUI start, never from `--run`.
- **`--run` waits for delivery:** it waits at most 5 s for pending macOS notifications before exiting.
- **Repo rules (AGENTS.md):**
  - `TreatWarningsAsErrors` in Core. The whole solution builds with 0 warnings.
  - Lock files are regenerated with SDK 10.0.301 and committed.
  - Never run a real maintenance `--run` without the user asking.

## Review Focus

1. **A notification fired from a background engine thread** (run completed) must not crash or deadlock on macOS. It needs an autorelease pool, and it must not touch AppKit's main thread. Task 3 handles this with the pool and `Task.Run`-safe interop.
2. **A null or empty title or message** must not reach Objective-C as nil. Task 3 substitutes `""`.
3. **A Sweepbox started from the DMG or Downloads** (outside `/Applications`) gets refused. It must stay silent and write to `Trace`. Task 3 handles it through the completion-block error path.
4. **A second GUI start or an update** must not show the permission prompt again. macOS persists the answer, so `requestAuthorization` is called only from GUI start (Task 3).
5. **The `net10.0` build must never reference Windows-only APIs.** `tools/Screenshots` and macOS publishing use it. Task 1/2 compiles the Windows code only for the Windows target, and the `net10.0` build gets a stub.

---

### Task 1: Windows target framework, packaging scripts and run commands

**Files:**
- Modify: `src/Sweepbox.Desktop/Sweepbox.Desktop.csproj`, `src/Sweepbox.Desktop/packages.lock.json` (regenerated)
- Modify: `scripts/package-windows.ps1:45` (add `-f net10.0-windows10.0.19041.0`), `scripts/package-macos.sh:55` (add `-f net10.0`)
- Modify: `AGENTS.md:34`, `README.md:67`, `docs/development.md:16-18` (`dotnet run` gains `-f net10.0`, with a note that Windows uses the Windows target)

**Interfaces:**
- Produces:
  - Desktop builds for both targets.
  - The compile symbol `WINDOWS` is defined only for the Windows target. The .NET SDK defines it automatically for `-windows` target frameworks; don't add it by hand.

- [ ] **Step 1: Change the csproj.**
  - Replace `<TargetFramework>net10.0</TargetFramework>` with `<TargetFrameworks>net10.0;net10.0-windows10.0.19041.0</TargetFrameworks>`.
  - Add `<EnableWindowsTargeting>true</EnableWindowsTargeting>`.
  - Add `<SupportedOSPlatformVersion Condition="'$(TargetFramework)' == 'net10.0-windows10.0.19041.0'">10.0.17763.0</SupportedOSPlatformVersion>`.
  - Keep `RuntimeIdentifiers` unchanged.
- [ ] **Step 2: Regenerate the lock files.**
  - Run `dotnet restore Sweepbox.slnx --force-evaluate`.
  - Expected: the Desktop lock file gains `net10.0-windows10.0.19041.0` sections, and the other lock files are unchanged or touched trivially.
- [ ] **Step 3: Build and test.**
  - Run `dotnet build Sweepbox.slnx -c Release -p:RestoreLockedMode=true`.
  - Expected: 0 warnings, and both targets of Sweepbox.Desktop built (two output folders under `src/Sweepbox.Desktop/bin/Release/`).
  - Run `dotnet test Sweepbox.slnx -c Release -p:RestoreLockedMode=true`. Expected: 97/97.
- [ ] **Step 4: Add `-f` to both packaging scripts.**
  - Run `ARCHS=arm64 scripts/package-macos.sh`.
  - Expected: a DMG is produced, and `artifacts/macos-arm64/Sweepbox.app/Contents/MacOS/Sweepbox --dry-run` prints the steps.
- [ ] **Step 5: Check the screenshot tool still builds.**
  - Run `dotnet build tools/Screenshots -c Release`.
  - Expected: success. It references the `net10.0` Desktop.
- [ ] **Step 6: Update the `dotnet run` lines in AGENTS.md, README.md and docs/development.md.**
- [ ] **Step 7: Commit:** "Build the Desktop app for a Windows-specific target as well".

### Task 2: Windows toasts under a Sweepbox AUMID

**Files:**
- Create: `src/Sweepbox.Desktop/Services/WindowsNotifications.cs`. The whole body is inside `#if WINDOWS`; the `#else` branch is a stub with the same members that do nothing.
- Modify: `src/Sweepbox.Desktop/Sweepbox.Desktop.csproj`: publish `Assets/app-icon.png` next to the executable as `app-icon.png`, with `CopyToPublishDirectory=PreserveNewest` and `ExcludeFromSingleFile=true`, like `LICENSE.txt`.
- Modify: `src/Sweepbox.Desktop/Services/Notifier.cs`: delete `ShowWindows`; on Windows call `WindowsNotifications.Show`.
- Modify: `src/Sweepbox.Desktop/App.axaml.cs` (start of `OnFrameworkInitializationCompleted`) and `src/Sweepbox.Desktop/Program.cs` (`HeadlessRun`): call `WindowsNotifications.Register()` on Windows.
- Modify: `installer/windows/Sweepbox.iss` `[Code]`: add `CurUninstallStepChanged`. On `usPostUninstall` it calls `RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\AppUserModelId\dk.sweepbox.desktop')`. If a `CurUninstallStepChanged` already exists, extend it.

**Interfaces:**
- Produces:
  - `internal static class WindowsNotifications` with `public static void Register()` and `public static void Show(string title, string message)`.
  - `internal const string AppUserModelId = "dk.sweepbox.desktop";`

- [ ] **Step 1: Implement `Register()`.**
  - Use `Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\dk.sweepbox.desktop")`.
  - Set `DisplayName` = `Sweepbox` and `IconUri` = `Path.Combine(AppContext.BaseDirectory, "app-icon.png")`, both as strings.
  - Wrap it in `try/catch`, writing failures to `Trace`.
- [ ] **Step 2: Implement `Show`.**
  - Build `<toast><visual><binding template="ToastGeneric"><text>{title}</text><text>{message}</text></binding></visual></toast>`, escaping both with `SecurityElement.Escape`.
  - Load it into a `Windows.Data.Xml.Dom.XmlDocument` and call `ToastNotificationManager.CreateToastNotifier(AppUserModelId).Show(new ToastNotification(doc))`.
  - Use `try/catch` to `Trace`.
- [ ] **Step 3: Wire it in.** Change `Notifier.Show` and add the `Register()` calls. Delete `ShowWindows` and its PowerShell script.
- [ ] **Step 4: Update the installer.** Add the `[Code]` uninstall handler. There is no `[Registry]` entry.
- [ ] **Step 5: Build.**
  - Run `dotnet build src/Sweepbox.Desktop -c Release -f net10.0-windows10.0.19041.0 -p:RestoreLockedMode=true`. Expected: 0 warnings.
  - Run `dotnet build Sweepbox.slnx -c Release -p:RestoreLockedMode=true`. Expected: 0 warnings.
  - Run `dotnet publish src/Sweepbox.Desktop -c Release -f net10.0-windows10.0.19041.0 -r win-x64 --self-contained -o <scratch>/win`. Expected: `app-icon.png` next to `Sweepbox.exe`.
- [ ] **Step 6: Commit:** "Show Windows toasts natively under a Sweepbox AppUserModelID". CI's Windows job builds the installer; the Windows behaviour itself is checked by hand by the user (spec Tests §3).

### Task 3: macOS notifications through UNUserNotificationCenter

**Files:**
- Create: `src/Sweepbox.Desktop/Services/MacNotifications.cs`.
- Modify: `src/Sweepbox.Desktop/Services/Notifier.cs`:
  - delete `ShowMac` and `AppleScriptString`;
  - on macOS call `MacNotifications.Show`;
  - add `public static Task FlushAsync(TimeSpan timeout)`.
- Modify: `src/Sweepbox.Desktop/App.axaml.cs`: on macOS, at start, call `MacNotifications.Initialize(requestPermission: true)`.
- Modify: `src/Sweepbox.Desktop/Program.cs` `HeadlessRun`:
  - on macOS call `MacNotifications.Initialize(requestPermission: false)` before the run;
  - after the run, `Notifier.FlushAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult()`.

**Interfaces:**
- Produces:
  - `internal static class MacNotifications` with:
    - `public static void Initialize(bool requestPermission)`
    - `public static Task Show(string title, string message)`. It completes when the completion block fires, or at once if notifications are unavailable.
    - `public static Task WhenIdleAsync(TimeSpan timeout)`
  - `Notifier.FlushAsync` delegates to `WhenIdleAsync` on macOS and completes at once elsewhere.

Implementation decisions (from the spec; the builder writes the code):

- **Interop:**
  - `[LibraryImport("/usr/lib/libobjc.dylib")]` for `objc_getClass`, `sel_registerName`, `objc_msgSend` (typed overloads per signature), `objc_allocateClassPair`, `class_addMethod`, `objc_registerClassPair` and `class_addProtocol`/`objc_getProtocol`.
  - `dlopen` for `/System/Library/Frameworks/UserNotifications.framework/UserNotifications`.
  - Make `NSString`s with `stringWithUTF8String:`.
- **Availability:** `[[NSBundle mainBundle] bundleIdentifier]` is non-null, and `UNUserNotificationCenter` resolves. Otherwise write one `Trace` line and every `Show` returns a completed task.
- **Blocks:**
  - A static global block literal: `isa` = `&_NSConcreteGlobalBlock` from `libSystem` via `dlsym`, flags `BLOCK_IS_GLOBAL` (1<<28), an `[UnmanagedCallersOnly]` invoke and a static descriptor.
  - The completion handler for `requestAuthorization` has the signature `(Block*, bool granted, NSError* error)` and writes to `Trace`.
  - The handler for `add…` has the signature `(Block*, NSError*)`. It writes the error to `Trace` and decrements a pending counter.
  - Per-request correlation isn't needed: keep an `int pending` (Interlocked) and a `TaskCompletionSource` for "idle". `Show` returns a task that completes when `pending` returns to 0.
- **Delegate:**
  - A class `SweepboxNotificationDelegate : NSObject`, adopting `UNUserNotificationCenterDelegate`.
  - It implements `userNotificationCenter:willPresentNotification:withCompletionHandler:` with type encoding `v@:@@@?`.
  - The method calls the handler block's invoke with options `Banner (1<<4) | List (1<<3) | Sound (1<<1)`.
  - One instance is set with `setDelegate:` and kept in a static field for the app's lifetime.
  - It's set in `Initialize` before `requestAuthorization`.
- **Threads:** every entry point creates an `NSAutoreleasePool` (`[[NSAutoreleasePool alloc] init]` … `drain`). The UserNotifications calls are thread-safe and need no main thread.

- [ ] **Step 1: Implement `MacNotifications`** per the decisions above.
- [ ] **Step 2: Wire it in.** Change `Notifier` (delete the osascript code), `App` and `Program.HeadlessRun`.
- [ ] **Step 3: Build.**
  - Run `dotnet build Sweepbox.slnx -c Release -p:RestoreLockedMode=true`. Expected: 0 warnings.
  - Run `dotnet test …`. Expected: 97/97.
- [ ] **Step 4: Dev-run smoke test.**
  - Run `dotnet run --project src/Sweepbox.Desktop -f net10.0 -- --dry-run`.
  - Expected: it prints the steps and doesn't crash. This also proves the `net10.0` path loads without a bundle.
- [ ] **Step 5: Hand over for manual verification (controller + user, not the implementer).**
  - Package with `ARCHS=arm64 scripts/package-macos.sh` and install to `/Applications`.
  - Expected:
    - First launch shows one permission prompt titled with Sweepbox's name and icon.
    - **Check for Updates** shows "Sweepbox is up to date" with the Sweepbox icon, both when Sweepbox is in the background and when the Status window has focus.
    - Running the DMG copy directly doesn't crash.
  - The implementer stops after Step 4 and reports. The controller drives this step with the user.
- [ ] **Step 6: Commit:** "Post macOS notifications natively through UNUserNotificationCenter".

### Task 4: Documentation

**Files:**
- `docs/architecture.md`:
  - replace the notifications helper line in the Desktop table with `MacNotifications` / `WindowsNotifications`;
  - add the Windows target to the process model.
- `docs/development.md`: add a section on the two Desktop targets and `-f`.
- `docs/releasing.md`: note the `-f` flags in the macOS and Windows packaging sections.
- `user-docs/MANUAL.md`:
  - notifications appear as Sweepbox in System Settings / Windows Settings, and macOS asks once on first launch;
  - Troubleshooting: on macOS, notifications only work when Sweepbox is in the Applications folder.

- [ ] **Step 1: Write the doc changes.**
- [ ] **Step 2: Verify the website still builds.** Run `python3 tools/build_site.py` with markdown installed. Expected: "Site built". Remove `_site/` afterwards.
- [ ] **Step 3: Commit:** "Document native notifications and the Windows target".
