#!/bin/bash
# Builds MeetingNudge from this folder into ~/Applications and starts it.
# Building on your own Mac means no "unidentified developer" warning.
set -euo pipefail
cd "$(dirname "$0")"

if ! xcrun --find swiftc >/dev/null 2>&1; then
  echo "MeetingNudge needs Apple's free Command Line Tools to build."
  echo "A macOS popup will appear: click Install, wait for it to finish, then run the install command again."
  xcode-select --install || true
  exit 1
fi

APP="$HOME/Applications/MeetingNudge.app"
echo "Building MeetingNudge…"
BUILD="$(mktemp -d)"
swiftc -O -swift-version 5 -o "$BUILD/MeetingNudge" main.swift ICS.swift Overlay.swift Settings.swift

# Stop a running copy before replacing it.
osascript -e 'tell application id "app.meetingnudge" to quit' >/dev/null 2>&1 || true
pkill -x MeetingNudge 2>/dev/null || true

mkdir -p "$APP/Contents/MacOS"
cp Info.plist "$APP/Contents/Info.plist"
mv "$BUILD/MeetingNudge" "$APP/Contents/MacOS/MeetingNudge"
codesign --force --sign - "$APP" >/dev/null 2>&1
rm -rf "$BUILD"

open "$APP"
echo "✅ MeetingNudge is installed and running — look for the ⏰ in your menu bar."
