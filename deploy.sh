#!/usr/bin/env bash
# Rebuilds the bridge, installs the addon into WoW and (re)starts the bridge as a LaunchAgent.
# Usage: ./deploy.sh            (ADDONS=<path to Interface/AddOns> to override)
# Stop for good: launchctl bootout gui/$(id -u)/com.spacentropy.mrglglaude
set -euo pipefail
cd "$(dirname "$0")"
ADDONS="${ADDONS:-/Applications/World of Warcraft/_forever_/Interface/AddOns}"
LABEL=com.spacentropy.mrglglaude
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"

build=$(mktemp -d)
trap 'rm -rf "$build"' EXIT
dotnet publish ClaudeForeverBridge.cs -o "$build"
"$build/ClaudeForeverBridge" --selftest

mkdir -p bin "$ADDONS/ClaudeForever" "$(dirname "$PLIST")"
# mv gives the binary a new inode; overwriting a signed binary in place gets it killed by macOS.
mv -f "$build/ClaudeForeverBridge" bin/
cp ClaudeForever/* "$ADDONS/ClaudeForever/"

# plutil escapes the paths, which may contain spaces or XML characters.
rm -f "$PLIST"
plutil -create xml1 "$PLIST"
plutil -insert Label -string "$LABEL" "$PLIST"
plutil -insert ProgramArguments -array "$PLIST"
plutil -insert ProgramArguments -string "$PWD/bin/ClaudeForeverBridge" -append "$PLIST"
plutil -insert ProgramArguments -string "$ADDONS" -append "$PLIST"
plutil -insert RunAtLoad -bool YES "$PLIST"
plutil -insert KeepAlive -bool YES "$PLIST"
plutil -insert StandardOutPath -string "$PWD/bridge.log" "$PLIST"
plutil -insert StandardErrorPath -string "$PWD/bridge.log" "$PLIST"

launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
pkill -f 'bin/ClaudeForeverBridge' || true # a copy started by hand from a terminal
: > bridge.log
started=$(mktemp "$build/started.XXXX")
launchctl bootstrap "gui/$(id -u)" "$PLIST"
sleep 3
cat bridge.log

# The bridge writes the current slot at startup; no fresh Feed.lua means it can't write into the WoW folder.
if [ -z "$(find "$ADDONS" -maxdepth 2 -name Feed.lua -newer "$started")" ]; then
  echo "The bridge can't write to $ADDONS." >&2
  echo "If the log says access is denied, allow ClaudeForeverBridge in System Settings > Privacy & Security > App Management, then rerun ./deploy.sh." >&2
  exit 1
fi
echo "Bridge running as LaunchAgent $LABEL (starts at login), log: $PWD/bridge.log. In WoW: /reload"
