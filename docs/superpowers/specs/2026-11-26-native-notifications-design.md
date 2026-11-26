# Design: Native notifications on macOS and Windows

Date: 2026-11-26 · Status: draft for approval

## Intent

Notifications should come from Sweepbox itself, with its name and icon. Today they don't:

- **macOS:** `Notifier.ShowMac` runs `osascript -e 'display notification …'`. macOS credits the notification to Script Editor, which shows Script Editor's icon. The title also appears twice, because "Sweepbox" is passed as the title and the real title as the subtitle.
- **Windows:** `Notifier.ShowWindows` starts PowerShell and shows the toast under PowerShell's AppUserModelID. Windows shows it as "Windows PowerShell".

Success means a Sweepbox notification shows "Sweepbox" with the app icon, and appears in the system's notification settings as Sweepbox, where the user can turn it off.

## Goals

- **macOS:** notifications are posted in-process through `UNUserNotificationCenter`, with no helper process.
- **Windows:** toasts are shown in-process through the Windows toast API, under a Sweepbox AppUserModelID registered for the current user, with no helper process.
- **Callers unchanged:** `Notifier.Show(title, message)` keeps its signature, so all seven call sites stay as they are. The title and message come through unchanged, and "Sweepbox" no longer appears as a separate title line.
- **Never throws:** notification failures are written to `Trace`, as today.

## Non-goals

- Clicking a notification does nothing beyond the OS default. There are no actions and no click handling (no COM activator on Windows).
- No notification grouping or replacement, and no sounds or custom images.
- No Linux.
- No change to *when* notifications are sent, which is decided by the existing settings.

## Anti-goals

- **Falling back to `osascript` or PowerShell.** Bringing back the Script Editor / PowerShell attribution is the failure this fixes.
- **Crashing or hanging** when notifications are unavailable: permission denied, a dev build run outside a bundle, Focus mode, or an older Windows.
- **Asking for permission in the middle of a run,** or more than once.

## Approaches considered

| | Approach | Trade-off |
|---|---|---|
| **macOS A (chosen)** | `UNUserNotificationCenter` through the Objective-C runtime (P/Invoke `objc_msgSend`, blocks and a delegate class built at runtime) | Current Apple API, no new package or process. The interop code is the cost |
| macOS B | `NSUserNotificationCenter` | Simpler interop, but deprecated since macOS 11 and may disappear |
| macOS C | A bundled Swift helper app | A second compiled and signed program in CI, for the same result |
| **Windows A (chosen)** | A Windows-specific .NET target for the Windows build, which unlocks `Windows.UI.Notifications.ToastNotificationManager`, plus a per-user AUMID registration | No helper process. The build configuration, packaging script and lock files change |
| Windows B | Keep PowerShell, but send under a Sweepbox AUMID | Little work and correct attribution, but still one process per toast, and some company policies block it |

The user picked **A for both**.

## Spike result: ad-hoc signing (2026-11-26)

Every release is ad-hoc signed and not notarized, so before building macOS A we tested whether macOS lets such an app post notifications at all. The test was a throwaway Swift app bundle (`dk.sweepbox.spike`), ad-hoc signed exactly like `package-macos.sh` does, on macOS 26.7, calling `requestAuthorization` and then `add`.

- **Run from a temp folder:** refused at once, with no prompt: `UNErrorDomain Code=1 "Notifications are not allowed for this application"`.
- **Run from `/Applications`:** macOS showed its permission prompt, with the app's own name and icon. After **Allow**: `granted=true`, `authorizationStatus=2`, `add` returned no error, and the notification appeared.

**Conclusion:** macOS A works for the released app, which is installed in `/Applications`; no Developer ID is needed. Copies outside `/Applications` are refused. That includes dev builds and a release run from the DMG or Downloads, and Sweepbox must handle it quietly (see Availability).

## Design

### macOS: `MacNotifications` (new, `src/Sweepbox.Desktop/Services/`)

- **Interop:** `objc_getClass`, `sel_registerName` and `objc_msgSend` from `/usr/lib/libobjc.dylib`. The UserNotifications framework is loaded with `dlopen("/System/Library/Frameworks/UserNotifications.framework/UserNotifications")`, and the app already links AppKit through Avalonia.
- **Availability:** `UNUserNotificationCenter` only works for code running inside an app bundle. `IsAvailable` is true only when `[NSBundle mainBundle].bundleIdentifier` is non-null; that check happens before `currentNotificationCenter` is touched, because touching it outside a bundle raises an exception. A dev build run with `dotnet run` therefore sends no notifications, and writes one `Trace` line instead. The installed app, and the `--run` CLI started from the bundle's binary, both have a bundle. A bundle outside `/Applications` *is* available but is refused by macOS (see the spike). That arrives as an error in the completion handlers, goes to `Trace`, and is never shown to the user.
- **Objective-C exceptions abort the process.** An exception raised inside `objc_msgSend` is not turned into a .NET exception, so `Notifier`'s `try/catch` can't catch it. Every call is therefore guarded before it is made: check classes and selectors exist (`objc_getClass` / `respondsToSelector:` non-null), never pass nil where Apple's API requires an object (substitute `""` for a null or empty title or body), and add the delegate method with its exact type encoding.
- **Permission:** call `requestAuthorizationWithOptions:(alert | sound)` once at app start, in `App.OnFrameworkInitializationCompleted`, only when `IsAvailable`.
  - macOS shows its prompt only the first time; later calls return the stored answer without asking again.
  - The completion handler is an Objective-C block built in C#: a block literal and descriptor in unmanaged memory, with an `[UnmanagedCallersOnly]` invoke function. It writes the outcome to `Trace`. No permission state is cached; posting doesn't depend on it.
  - The `--run` CLI path never requests permission. Without permission, a post fails quietly, as the spike showed. It never prompts.
- **Posting:**
  1. Build a `UNMutableNotificationContent` with `title` = the caller's title and `body` = the message.
  2. Build a `UNNotificationRequest` with a new GUID string as identifier and a nil trigger.
  3. Call `addNotificationRequest:withCompletionHandler:` with a completion block that writes any error to `Trace` and signals a wait handle.
  4. Wrap the calls in an `NSAutoreleasePool`, so this works from the background threads the engine events arrive on.
- **`--run` must not exit before delivery.** `add` hands the request to the system asynchronously, and the CLI exits right after the run. `MacNotifications.Show` therefore returns a `Task` that completes when the completion block fires. `Notifier.Show` stays fire-and-forget for the GUI. `HeadlessRun` waits for pending notifications for up to 5 s before returning: `Notifier.FlushAsync(TimeSpan)`, a no-op on Windows.
- **Showing while Sweepbox is active:** without a delegate, macOS hides notifications while the app is frontmost, for example when the Status window has focus. At startup, Sweepbox creates and sets a delegate class (`objc_allocateClassPair` on `NSObject`, adding `userNotificationCenter:willPresentNotification:withCompletionHandler:`). The method invokes the handler block with `UNNotificationPresentationOptionBanner | List | Sound`. The delegate object is kept alive for the app's lifetime.
- **Icon and name** come from the bundle (`CFBundleName`, `AppIcon`). No Info.plist change is needed. `CFBundleIdentifier` is already `dk.sweepbox.desktop`.

### Windows: target and `WindowsNotifications` (new)

- **Target framework:** `Sweepbox.Desktop.csproj` changes to `<TargetFrameworks>net10.0;net10.0-windows10.0.19041.0</TargetFrameworks>`, with `<EnableWindowsTargeting>true</EnableWindowsTargeting>` so macOS (and CI's macOS jobs) can build the Windows target.
  - **Both targets on every OS** (controller's call). Making the Windows target conditional on the OS would give different lock files per OS, and that breaks `RestoreLockedMode`. The cost is that `dotnet run --project src/Sweepbox.Desktop` needs `-f net10.0` (macOS) or `-f net10.0-windows10.0.19041.0` (Windows). Update the commands in AGENTS.md, README.md and docs/development.md to match.
  - Set `<SupportedOSPlatformVersion>10.0.17763.0</SupportedOSPlatformVersion>` for the Windows target, to match the installer's minimum Windows version.
  - The macOS publish uses `-f net10.0`; the Windows publish uses `-f net10.0-windows10.0.19041.0`. Both packaging scripts pass `-f` explicitly.
  - The Windows-only code compiles only for the Windows target (`#if WINDOWS` or a `Condition` on its files). The `net10.0` build keeps a stub, so macOS behaviour and `tools/Screenshots` (which references the `net10.0` Desktop) are unaffected.
  - Lock files are regenerated with SDK 10.0.301. The Desktop lock file gains a `net10.0-windows10.0.19041.0` section for each runtime ID. The Windows SDK projection comes in as a targeting pack and may not appear there.
- **AUMID:** the value is `dk.sweepbox.desktop`, the same id as on macOS. At startup in the GUI app, and also in `--run`, `WindowsNotifications.Register()` writes `HKCU\Software\Classes\AppUserModelId\dk.sweepbox.desktop` with:
  - `DisplayName` = `Sweepbox`;
  - `IconUri` = the full path of `app-icon.png` next to the executable.

  It is written on every start, so a moved or updated install stays correct. A failure goes to `Trace`.
- **Icon file:** `Assets/app-icon.png` is also published next to the executable (`CopyToPublishDirectory`, `ExcludeFromSingleFile`), as `LICENSE.txt` already is. The Inno Setup script already installs everything from the publish folder; check that it does and adjust if not.
- **Posting:** build the toast XML (`ToastGeneric`, two `<text>` elements: the title and the message, XML-escaped), then `ToastNotificationManager.CreateToastNotifier("dk.sweepbox.desktop").Show(new ToastNotification(xml))`. Exceptions go to `Trace`.
- **Uninstall clean-up** (controller's call): a `[Code]` `CurUninstallStepChanged(usPostUninstall)` handler deletes `HKCU\Software\Classes\AppUserModelId\dk.sweepbox.desktop` for the user running the uninstaller (`RegDeleteKeyIncludingSubkeys`).
  - It is not a `[Registry]` entry, because that would also create the key at install time.
  - In an all-users install, other users who ran Sweepbox keep their own key: just a display name and an icon path. That leftover is accepted.

### `Notifier`

- `Show` dispatches to `MacNotifications.Show` or `WindowsNotifications.Show`.
- `ShowMac`, `ShowWindows`, `AppleScriptString` and the PowerShell script are deleted.
- `OpenPath` is unchanged.

### Tests and checks

This code lives in Desktop, which has no test project, and it talks to the OS directly. So there are no automated tests for it; the build in both targets is the automated check. It is verified by hand:

1. **macOS, installed build:**
   - first launch shows one permission prompt;
   - **Check for Updates** while up to date shows "Sweepbox is up to date" with the Sweepbox icon, both when Sweepbox is in the background and when the Status window has focus;
   - after the permission is denied in System Settings, nothing crashes and a `Trace` line is written;
   - `--run` from the bundle binary posts its result before the process exits;
   - a copy run from outside `/Applications` (for example straight from the DMG) doesn't crash, shows no notification and writes a `Trace` line.
2. **macOS, `dotnet run -f net10.0`:** no crash and no notification.
3. **Windows 10/11, installed build:**
   - **Check for Updates** shows a toast whose header reads "Sweepbox" with the app icon, and whose text is the caller's title and message;
   - Sweepbox appears under Settings → Notifications;
   - after uninstalling, the registry key is gone.
4. **CI:** both installer jobs build. Build and test under `RestoreLockedMode` pass.

### Docs

- `docs/architecture.md`: replace the "Native notifications" helper line, and add the Windows target to the Desktop section and the process model.
- `docs/development.md`: explain the two Desktop targets and `-f`.
- `docs/releasing.md`: list the `-f` flags in packaging.
- `user-docs/MANUAL.md`: notifications appear under Sweepbox in System Settings / Windows Settings, and macOS asks for permission on first launch. Notifications on macOS need Sweepbox to be in the Applications folder, so add that to Troubleshooting.
- `AGENTS.md`, `README.md`: `dotnet run` commands gain `-f net10.0`.

## Left to the builder

- The exact interop types (`SafeHandle` vs `IntPtr`), and the block-literal layout code, following Apple's Block ABI.
- `#if WINDOWS` vs conditional `Compile` items.
- How Inno Setup picks up the icon file, as long as the installed folder contains `app-icon.png` next to `Sweepbox.exe`.
- Commit and PR breakdown.
