#!/bin/sh
# Reverts register-rewind-icon.sh — unregisters the helper app bundle and
# removes it from ~/Applications.
#
# Usage: ./unregister-rewind-icon.sh

set -e

APP_PATH="$HOME/Applications/RewindFileIcon.app"
LSREGISTER="/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"

if [ -d "$APP_PATH" ]; then
    "$LSREGISTER" -u "$APP_PATH" || true
    rm -rf "$APP_PATH"
    echo "Removed $APP_PATH"
else
    echo "Nothing to remove — $APP_PATH not found."
fi

killall Finder >/dev/null 2>&1 || true
