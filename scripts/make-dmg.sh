#!/usr/bin/env bash
# make-dmg.sh <App.app> <output.dmg> <background.png>   (a sibling background@2x.png is used for Retina)
# Builds a compressed DMG with the app, an Applications shortcut, a background with an arrow,
# and the app icon as volume icon. The Finder window layout is applied via AppleScript; if Finder
# automation is unavailable the DMG is still produced, just without the custom layout.
set -euo pipefail

APP="$1"; DMG="$2"; BACKGROUND="$3"
NAME="Sweepbox"
STAGE="$(mktemp -d)"
RW="$(mktemp -u).dmg"
MOUNT=""
cleanup() {
  [[ -n "$MOUNT" && -d "$MOUNT" ]] && hdiutil detach -force -quiet "$MOUNT" 2>/dev/null
  rm -rf "$STAGE" "$RW"
}
trap cleanup EXIT

cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
mkdir "$STAGE/.background"
# Finder draws a PNG at its pixel size (a 2x PNG shows twice too big), so combine 1x + 2x into one HiDPI TIFF.
BACKGROUND_2X="${BACKGROUND%.png}@2x.png"
if [[ -f "$BACKGROUND_2X" ]]; then
  tiffutil -cathidpicheck "$BACKGROUND" "$BACKGROUND_2X" -out "$STAGE/.background/background.tiff" >/dev/null 2>&1
else
  sips -s format tiff "$BACKGROUND" --out "$STAGE/.background/background.tiff" >/dev/null
fi

rm -f "$DMG"
hdiutil create -quiet -volname "$NAME" -srcfolder "$STAGE" -fs HFS+ -format UDRW -size 400m "$RW"

# Mount at a private path (not /Volumes/<name>) so an already-mounted Sweepbox DMG can't clash.
MOUNT="$(mktemp -d /tmp/sweepbox-dmg.XXXXXX)"
hdiutil attach -readwrite -noverify -noautoopen -nobrowse -mountpoint "$MOUNT" "$RW" >/dev/null

apply_layout() {
  # perl alarm = portable timeout, so a pending automation prompt can never hang the build.
  perl -e 'alarm 90; exec @ARGV' osascript - "$MOUNT" <<'EOF'
on run argv
  set mountPath to item 1 of argv
  tell application "Finder"
    set dmgFolder to (POSIX file mountPath) as alias
    open dmgFolder
    set w to container window of dmgFolder
    set current view of w to icon view
    set toolbar visible of w to false
    set statusbar visible of w to false
    set opts to the icon view options of w
    set arrangement of opts to not arranged
    set icon size of opts to 112
    set text size of opts to 13
    set background picture of opts to file ".background:background.tiff" of dmgFolder
    set position of item "Sweepbox.app" of dmgFolder to {180, 190}
    set position of item "Applications" of dmgFolder to {480, 190}
    -- 660x400 content (+28 title bar); set last so nothing above resizes it again
    set the bounds of w to {200, 120, 860, 548}
    update dmgFolder without registering applications
    delay 2
    close w
  end tell
end run
EOF
}

if apply_layout >/dev/null 2>&1 || { sleep 3; apply_layout >/dev/null 2>&1; }; then
  echo "    dmg: Finder layout applied"
else
  echo "    dmg: WARNING - Finder layout could not be applied (automation denied?); DMG has default layout"
fi

# Volume icon: added after the layout step, because Finder removes it while applying the layout.
# SetFile (Command Line Tools) marks the volume as having a custom icon.
cp "$APP/Contents/Resources/AppIcon.icns" "$MOUNT/.VolumeIcon.icns"
if xcrun --find SetFile >/dev/null 2>&1; then
  xcrun SetFile -a C "$MOUNT"
fi

rm -rf "$MOUNT/.fseventsd" "$MOUNT/.Trashes"
sync
hdiutil detach -quiet "$MOUNT" || hdiutil detach -force -quiet "$MOUNT"
MOUNT=""

hdiutil convert -quiet "$RW" -format UDZO -imagekey zlib-level=9 -o "$DMG"
