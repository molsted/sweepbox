# Releasing, CI and distribution

## Version

The app version is `<Version>` in `Directory.Build.props`. It sets the assembly and informational version and the installer file names. `IncludeSourceRevisionInInformationalVersion` is off because NetSparkle compares the informational version with the appcast, and a `+commit` suffix would break that comparison.

## Workflows

| Workflow | Runs on | What it does |
|---|---|---|
| `build.yml` | Push to `main`, PRs, `v*` tags, manual | `test` (macOS) → `macos` (DMGs + update zips) and `windows` (setup exes) → `release` (tags only) |
| `pages.yml` | Changes to `site/`, `user-docs/`, `LICENSE`, site tools, tray icons. Published releases. Manual | Builds `_site/` with `tools/build_site.py` and deploys it to GitHub Pages |
| `codeql.yml` | Push to `main`, PRs, weekly (Monday 04:00 UTC) | CodeQL code scanning |
| `sparkle-keys.yml` | Manual | Checks that the committed Sparkle public key is present. It never handles the private key |

`build.yml` skips pushes and PRs that only touch the website or documentation (`site/`, `docs/`, `user-docs/`, site tools). Path filters don't apply to tags, so a release tag always builds.

Workflow rules:

- Permissions are least-privilege. The default is `contents: read`, and only the `release` job gets `contents: write`.
- Third-party actions are pinned to a commit SHA, with the tag in a comment.
- Every `setup-dotnet` step pins `10.0.301`. See [development.md](development.md#dependencies-and-lock-files).

## Making a release

1. Bump `<Version>` in `Directory.Build.props` in a PR and merge it.
2. Tag the merge commit `v<version>` (for example `v1.2.2`) and push the tag.
3. `build.yml` builds everything. The `release` job then:
   1. Downloads the DMGs, update zips and setup exes.
   2. Generates one **signed appcast per OS and architecture** with `netsparkle-generate-appcast`. The appcasts are signed with the `SPARKLE_PRIVATE_KEY` secret. If the secret is missing, the job fails on purpose: an unsigned appcast would strand every existing install on the old version.
   3. Creates the GitHub release with a download table and generated release notes, and attaches all files.
4. Publishing the release triggers `pages.yml`. The website's download buttons read the latest release from the GitHub API, so they never need editing.

Only maintainers tag releases. Agents should not tag, release or bump the version unless asked.

## Artifacts

| File | Built by | Used for |
|---|---|---|
| `Sweepbox-<ver>-macos-{arm64,x64}.dmg` | `scripts/package-macos.sh` | Manual installs (drag to Applications) |
| `Sweepbox-<ver>-macos-{arm64,x64}.zip` | `scripts/package-macos.sh` (`ditto`) | The in-app updater on macOS |
| `Sweepbox-<ver>-windows-{x64,arm64}-setup.exe` | `scripts/package-windows.ps1` (Inno Setup, `installer/windows/Sweepbox.iss`) | Installs and in-app updates on Windows. Per-user by default, no admin needed |
| `appcast-{macos,windows}-{arm64,x64}.xml` (+ `.signature`) | `release` job | The update feed `AppUpdater` reads from `releases/latest/download/` |

The app is published **self-contained and single-file** for each runtime identifier.

## macOS packaging

```bash
scripts/package-macos.sh                      # arm64 + x64
ARCHS="arm64" scripts/package-macos.sh        # one architecture
```

- The script publishes with `-f net10.0`.

- `installer/macos/Info.plist` and `entitlements.plist` define the bundle (`LSUIElement`-style menu bar app).
- With Xcode 26+, `actool` compiles `installer/macos/AppIcon.icon` (Icon Composer) into `Assets.car`, so macOS 26 shows the icon natively. Without it the `.icns` is used, which macOS 26 shows inside a grey frame. CI selects the newest Xcode. Locally this also needs the Xcode license accepted and `xcode-select` pointing at Xcode.
- **Signing:** ad-hoc by default. Set `SIGN_IDENTITY="Developer ID Application: …"` to sign, and add `NOTARY_PROFILE=<profile>` (from `xcrun notarytool store-credentials`) to notarize and staple. Release builds are currently **not notarized**, which is why the release notes tell users to right-click → Open the first time.

## Windows packaging

```powershell
./scripts/package-windows.ps1                 # x64 + arm64
```

- The script publishes with `-f net10.0-windows10.0.19041.0`. `app-icon.png` is shipped next to `Sweepbox.exe`, because the toast registration points at it.
- Needs Inno Setup 6 (`winget install JRSoftware.InnoSetup`). The script finds `ISCC.exe` on PATH or in the usual install folders. CI installs it with Chocolatey if it is missing.
- Set `$env:SIGN_CERT_THUMBPRINT` to Authenticode-sign the exe and the installer with `signtool`.

## The in-app updater and its keys

- `installer/sparkle-public-key.txt` holds the Ed25519 public key. It is embedded in the app as a resource, and every update must be signed with the matching private key.
- The private key exists only in the `SPARKLE_PRIVATE_KEY` repository secret. It was generated locally, as described in the header of `sparkle-keys.yml`. **Never generate, print, log or commit a private key, and never change the public key casually.** Installed copies trust only the key they shipped with, so a new key needs a release signed with the old key that carries the new one.

## Website

- `site/` holds the landing page, the shared layout (`_layout.html`), CSS, JS and images.
- `tools/build_site.py` copies `site/`, fills the `{{…}}` placeholders, renders `user-docs/MANUAL.md` to `docs.html` and `LICENSE` to `license.html`, and copies the manual images and tray icons. Any placeholder left unfilled fails the build.
- To build locally:

  ```bash
  pip install -r tools/requirements-site.txt && python3 tools/build_site.py
  ```

  The output goes to `_site/`, which is gitignored.

Developer docs in `docs/` are **not** published on the website.
