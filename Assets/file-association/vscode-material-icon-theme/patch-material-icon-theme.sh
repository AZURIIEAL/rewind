#!/bin/sh
# Patches an installed "Material Icon Theme" (pkief.material-icon-theme) VS Code
# extension to show a custom icon for .rewind files.
#
# Material Icon Theme's `files.associations` setting only remaps to icon names
# from its own built-in set — it has no supported way to point at an arbitrary
# external image. This script edits the extension's generated icon-theme JSON
# directly and drops our icon file into its icons/ folder instead.
#
# Caveat: a Material Icon Theme extension update will overwrite this patch.
# Re-run this script to reapply it. Run restore-material-icon-theme.sh to revert
# to the extension's original, unpatched theme file.
#
# Usage: ./patch-material-icon-theme.sh

set -e

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
ICON_SRC="$SCRIPT_DIR/../../icons/file-icon-128.png"

EXT_DIR=$(find "$HOME/.vscode/extensions" "$HOME/.vscode-server/extensions" -maxdepth 1 -iname "pkief.material-icon-theme-*" -type d 2>/dev/null | sort -V | tail -1)

if [ -z "$EXT_DIR" ]; then
    echo "Material Icon Theme extension not found under ~/.vscode/extensions or ~/.vscode-server/extensions." >&2
    exit 1
fi

THEME_JSON="$EXT_DIR/dist/material-icons.json"
BACKUP_JSON="$EXT_DIR/dist/material-icons.json.rewind-original"

if [ ! -f "$THEME_JSON" ]; then
    echo "Could not find $THEME_JSON — extension layout may have changed." >&2
    exit 1
fi

if [ ! -f "$BACKUP_JSON" ]; then
    cp "$THEME_JSON" "$BACKUP_JSON"
fi

cp "$ICON_SRC" "$EXT_DIR/icons/rewind-file.png"

python3 - "$BACKUP_JSON" "$THEME_JSON" <<'PYEOF'
import json
import sys

backup_path, theme_path = sys.argv[1], sys.argv[2]

with open(backup_path) as f:
    theme = json.load(f)

theme["iconDefinitions"]["_file_rewind"] = {"iconPath": "./../icons/rewind-file.png"}
theme["fileExtensions"]["rewind"] = "_file_rewind"

with open(theme_path, "w") as f:
    json.dump(theme, f)
PYEOF

echo "Patched $THEME_JSON"
echo "Reload VS Code (Developer: Reload Window) or toggle the icon theme off and back on to see the change."
echo "Run restore-material-icon-theme.sh to revert, or re-run this script after a Material Icon Theme update wipes it."
