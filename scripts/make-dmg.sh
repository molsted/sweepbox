#!/usr/bin/env bash
# make-dmg.sh <App.app> <output.dmg> <background.png>
# Builds a compressed DMG with the app, an Applications shortcut and a background arrow.
# Finder layout is applied via AppleScript when possible (skipped in CI or if Finder automation is denied).
set -euo pipefail

APP="$1"; DMG="$2"; BACKGROUND="$3"
NAME="PkgCleaner"
STAGE="$(mktemp -d)"
RW="$(mktemp -u).dmg"
trap 'rm -rf "$STAGE" "$RW"' EXIT

cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
mkdir "$STAGE/.background"
cp "$BACKGROUND" "$STAGE/.background/background.png"

rm -f "$DMG"
hdiutil create -quiet -volname "$NAME" -srcfolder "$STAGE" -fs HFS+ -format UDRW -size 400m "$RW"

if [[ -z "${CI:-}" ]]; then
  MOUNT="$(hdiutil attach -readwrite -noverify -noautoopen "$RW" | awk -F'\t' '/\/Volumes\// {print $NF; exit}')"
  osascript >/dev/null 2>&1 <<EOF || echo "   (Finder layout skipped)"
tell application "Finder"
  tell disk "$(basename "$MOUNT")"
    open
    set current view of container window to icon view
    set toolbar visible of container window to false
    set statusbar visible of container window to false
    set the bounds of container window to {200, 120, 860, 520}
    set opts to the icon view options of container window
    set arrangement of opts to not arranged
    set icon size of opts to 112
    set background picture of opts to file ".background:background.png"
    set position of item "$NAME.app" of container window to {180, 200}
    set position of item "Applications" of container window to {480, 200}
    update without registering applications
    delay 1
    close
  end tell
end tell
EOF
  sync
  hdiutil detach -quiet "$MOUNT" || hdiutil detach -force -quiet "$MOUNT"
fi

hdiutil convert -quiet "$RW" -format UDZO -imagekey zlib-level=9 -o "$DMG"
