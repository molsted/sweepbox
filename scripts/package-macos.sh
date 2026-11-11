#!/usr/bin/env bash
# Builds Sweepbox.app and a drag-to-install DMG for each architecture.
#
#   scripts/package-macos.sh                 # arm64 + x64
#   ARCHS="arm64" scripts/package-macos.sh   # just Apple Silicon
#
# Signing (optional):
#   SIGN_IDENTITY="Developer ID Application: Name (TEAMID)"  sign with your certificate (default: ad-hoc)
#   NOTARY_PROFILE="profile"   notarize + staple, using a profile stored with `xcrun notarytool store-credentials`
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/src/Sweepbox.Desktop/Sweepbox.Desktop.csproj"
OUT="$ROOT/artifacts"
ARCHS="${ARCHS:-arm64 x64}"
SIGN_IDENTITY="${SIGN_IDENTITY:--}"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props")"

[[ -f "$ROOT/installer/macos/AppIcon.icns" ]] || python3 "$ROOT/tools/generate_icons.py"
mkdir -p "$OUT"

# macOS 26 shows icons that come only from an .icns inside a grey frame. Compiling the Icon
# Composer source (AppIcon.icon) into Assets.car needs actool from Xcode 26+; without it the
# plain .icns is kept (fine on macOS 12-15, framed on 26).
compile_icon() {
  local app="$1" tmp
  tmp="$(mktemp -d)"
  if xcrun --find actool >/dev/null 2>&1 &&
     xcrun actool "$ROOT/installer/macos/AppIcon.icon" --compile "$tmp" \
       --app-icon AppIcon --platform macosx --minimum-deployment-target 12.0 \
       --output-partial-info-plist "$tmp/partial.plist" --errors --warnings >"$tmp/actool.log" 2>&1 &&
     [[ -f "$tmp/Assets.car" ]]; then
    cp "$tmp/Assets.car" "$app/Contents/Resources/"
    [[ -f "$tmp/AppIcon.icns" ]] && cp "$tmp/AppIcon.icns" "$app/Contents/Resources/AppIcon.icns"
    /usr/libexec/PlistBuddy -c "Add :CFBundleIconName string AppIcon" "$app/Contents/Info.plist"
    echo "    icon: compiled AppIcon.icon with actool (macOS 26 native)"
  else
    echo "    icon: WARNING - Xcode 26 actool unavailable or failed; using legacy .icns (grey frame on macOS 26)"
    [[ -s "$tmp/actool.log" ]] && sed 's/^/      /' "$tmp/actool.log"
  fi
  rm -rf "$tmp"
}

for ARCH in $ARCHS; do
  echo "==> Building $ARCH"
  WORK="$OUT/macos-$ARCH"
  APP="$WORK/Sweepbox.app"
  rm -rf "$WORK"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

  # Not single-file: native dylibs must sit next to the executable inside the bundle to be signed.
  dotnet publish "$PROJECT" -c Release -r "osx-$ARCH" --self-contained \
    -p:PublishSingleFile=false -p:DebugType=none -o "$APP/Contents/MacOS" -nologo -v quiet

  sed "s/__VERSION__/$VERSION/g" "$ROOT/installer/macos/Info.plist" > "$APP/Contents/Info.plist"
  cp "$ROOT/installer/macos/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"
  compile_icon "$APP"

  echo "==> Signing ($([[ "$SIGN_IDENTITY" == "-" ]] && echo ad-hoc || echo "$SIGN_IDENTITY"))"
  SIGN_ARGS=(--force --timestamp --options runtime --entitlements "$ROOT/installer/macos/entitlements.plist" -s "$SIGN_IDENTITY")
  [[ "$SIGN_IDENTITY" == "-" ]] && SIGN_ARGS=(--force -s -)
  # .NET keeps managed dlls/json next to the executable, so every file in MacOS/ is signed
  # individually (as in Avalonia's macOS deployment guide), then the bundle itself.
  # The main executable is signed last, as part of the bundle.
  while IFS= read -r -d '' f; do
    [[ "$f" == "$APP/Contents/MacOS/Sweepbox" ]] && continue
    codesign "${SIGN_ARGS[@]}" "$f" 2>/dev/null
  done < <(find "$APP/Contents/MacOS" -type f -print0)
  codesign "${SIGN_ARGS[@]}" "$APP"
  codesign --verify --strict "$APP"

  echo "==> Creating DMG"
  DMG="$OUT/Sweepbox-$VERSION-macos-$ARCH.dmg"
  "$ROOT/scripts/make-dmg.sh" "$APP" "$DMG" "$ROOT/installer/macos/dmg-background.png"

  if [[ "$SIGN_IDENTITY" != "-" ]]; then
    codesign --force -s "$SIGN_IDENTITY" "$DMG"
    if [[ -n "${NOTARY_PROFILE:-}" ]]; then
      echo "==> Notarizing"
      xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait
      xcrun stapler staple "$DMG"
    fi
  fi
  echo "==> $DMG"
done
