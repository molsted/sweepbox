#!/usr/bin/env bash
# Builds PkgCleaner.app and a drag-to-install DMG for each architecture.
#
#   scripts/package-macos.sh                 # arm64 + x64
#   ARCHS="arm64" scripts/package-macos.sh   # just Apple Silicon
#
# Signing (optional):
#   SIGN_IDENTITY="Developer ID Application: Name (TEAMID)"  sign with your certificate (default: ad-hoc)
#   NOTARY_PROFILE="profile"   notarize + staple, using a profile stored with `xcrun notarytool store-credentials`
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/src/PkgCleaner.Desktop/PkgCleaner.Desktop.csproj"
OUT="$ROOT/artifacts"
ARCHS="${ARCHS:-arm64 x64}"
SIGN_IDENTITY="${SIGN_IDENTITY:--}"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props")"

[[ -f "$ROOT/installer/macos/AppIcon.icns" ]] || python3 "$ROOT/tools/generate_icons.py"
mkdir -p "$OUT"

for ARCH in $ARCHS; do
  echo "==> Building $ARCH"
  WORK="$OUT/macos-$ARCH"
  APP="$WORK/PkgCleaner.app"
  rm -rf "$WORK"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

  # Not single-file: native dylibs must sit next to the executable inside the bundle to be signed.
  dotnet publish "$PROJECT" -c Release -r "osx-$ARCH" --self-contained \
    -p:PublishSingleFile=false -p:DebugType=none -o "$APP/Contents/MacOS" -nologo -v quiet

  sed "s/__VERSION__/$VERSION/g" "$ROOT/installer/macos/Info.plist" > "$APP/Contents/Info.plist"
  cp "$ROOT/installer/macos/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"

  echo "==> Signing ($([[ "$SIGN_IDENTITY" == "-" ]] && echo ad-hoc || echo "$SIGN_IDENTITY"))"
  SIGN_ARGS=(--force --timestamp --options runtime --entitlements "$ROOT/installer/macos/entitlements.plist" -s "$SIGN_IDENTITY")
  [[ "$SIGN_IDENTITY" == "-" ]] && SIGN_ARGS=(--force -s -)
  # .NET keeps managed dlls/json next to the executable, so every file in MacOS/ is signed
  # individually (as in Avalonia's macOS deployment guide), then the bundle itself.
  # The main executable is signed last, as part of the bundle.
  while IFS= read -r -d '' f; do
    [[ "$f" == "$APP/Contents/MacOS/PkgCleaner" ]] && continue
    codesign "${SIGN_ARGS[@]}" "$f" 2>/dev/null
  done < <(find "$APP/Contents/MacOS" -type f -print0)
  codesign "${SIGN_ARGS[@]}" "$APP"
  codesign --verify --strict "$APP"

  echo "==> Creating DMG"
  DMG="$OUT/PkgCleaner-$VERSION-macos-$ARCH.dmg"
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
