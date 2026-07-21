#!/bin/sh
# Reverts patch-material-icon-theme.sh — restores Material Icon Theme's original
# generated theme JSON from the backup the patch script made.
#
# Usage: ./restore-material-icon-theme.sh

set -e

EXT_DIR=$(find "$HOME/.vscode/extensions" "$HOME/.vscode-server/extensions" -maxdepth 1 -iname "pkief.material-icon-theme-*" -type d 2>/dev/null | sort -V | tail -1)

if [ -z "$EXT_DIR" ]; then
    echo "Material Icon Theme extension not found." >&2
    exit 1
fi

THEME_JSON="$EXT_DIR/dist/material-icons.json"
BACKUP_JSON="$EXT_DIR/dist/material-icons.json.rewind-original"

if [ ! -f "$BACKUP_JSON" ]; then
    echo "No backup found at $BACKUP_JSON — nothing to restore." >&2
    exit 1
fi

cp "$BACKUP_JSON" "$THEME_JSON"
rm -f "$EXT_DIR/icons/rewind-file.png"

echo "Restored $THEME_JSON to its original state."
echo "Reload VS Code (Developer: Reload Window) to see the change."
