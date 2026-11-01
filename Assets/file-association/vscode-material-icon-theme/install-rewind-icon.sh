#!/bin/sh
# Shows a custom icon for .rewind files in VS Code's Explorer when using the
# "Material Icon Theme" (pkief.material-icon-theme) extension.
#
# Uses the theme's supported "custom SVG icons" feature: the SVG is copied into
# a folder inside ~/.vscode/extensions, and a user setting points *.rewind at it.
# Unlike patching the extension, this survives Material Icon Theme updates.
#
# Usage: ./install-rewind-icon.sh      Run uninstall-rewind-icon.sh to revert.

set -e

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
ICON_SRC="$SCRIPT_DIR/../../icons/rewind-file.svg"

# The theme resolves custom icon paths relative to its own dist/ folder, so
# "../../rewind-icons/rewind-file" means <extensions dir>/rewind-icons/rewind-file.svg.
installed=0
for EXT_ROOT in "$HOME/.vscode/extensions" "$HOME/.vscode-server/extensions"; do
    if [ -d "$EXT_ROOT" ]; then
        mkdir -p "$EXT_ROOT/rewind-icons"
        cp "$ICON_SRC" "$EXT_ROOT/rewind-icons/rewind-file.svg"
        echo "Copied icon to $EXT_ROOT/rewind-icons/rewind-file.svg"
        installed=1
    fi
done

if [ "$installed" -eq 0 ]; then
    echo "No VS Code extensions folder found under ~/.vscode or ~/.vscode-server." >&2
    exit 1
fi

case "$(uname -s)" in
    Darwin) SETTINGS="$HOME/Library/Application Support/Code/User/settings.json" ;;
    *) SETTINGS="${XDG_CONFIG_HOME:-$HOME/.config}/Code/User/settings.json" ;;
esac

python3 - "$SETTINGS" <<'PYEOF'
import json
import os
import sys

path = sys.argv[1]
snippet = '"material-icon-theme.files.associations": { "*.rewind": "../../rewind-icons/rewind-file" }'

try:
    with open(path) as f:
        settings = json.load(f) if os.path.getsize(path) else {}
except FileNotFoundError:
    settings = {}
except json.JSONDecodeError:
    # settings.json allows comments and trailing commas; don't risk rewriting a file we can't parse.
    print(f"Could not parse {path} automatically. Add this to it by hand:\n  {snippet}")
    sys.exit(0)

associations = settings.setdefault("material-icon-theme.files.associations", {})
associations["*.rewind"] = "../../rewind-icons/rewind-file"

os.makedirs(os.path.dirname(path), exist_ok=True)
with open(path, "w") as f:
    json.dump(settings, f, indent=4)
    f.write("\n")
print(f"Updated {path}")
PYEOF

echo "Done. If the icon doesn't appear right away, run 'Developer: Reload Window' in VS Code."
