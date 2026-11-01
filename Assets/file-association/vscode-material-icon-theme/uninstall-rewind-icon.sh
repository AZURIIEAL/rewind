#!/bin/sh
# Reverts install-rewind-icon.sh: removes the copied icon and the *.rewind setting.
#
# Usage: ./uninstall-rewind-icon.sh

set -e

for EXT_ROOT in "$HOME/.vscode/extensions" "$HOME/.vscode-server/extensions"; do
    if [ -d "$EXT_ROOT/rewind-icons" ]; then
        rm -f "$EXT_ROOT/rewind-icons/rewind-file.svg"
        rmdir "$EXT_ROOT/rewind-icons" 2>/dev/null || true
        echo "Removed icon from $EXT_ROOT/rewind-icons"
    fi
done

case "$(uname -s)" in
    Darwin) SETTINGS="$HOME/Library/Application Support/Code/User/settings.json" ;;
    *) SETTINGS="${XDG_CONFIG_HOME:-$HOME/.config}/Code/User/settings.json" ;;
esac

python3 - "$SETTINGS" <<'PYEOF'
import json
import sys

path = sys.argv[1]
key = "material-icon-theme.files.associations"

try:
    with open(path) as f:
        settings = json.load(f)
except (FileNotFoundError, json.JSONDecodeError):
    print(f"Remove the \"*.rewind\" entry under \"{key}\" in {path} by hand if it's there.")
    sys.exit(0)

associations = settings.get(key, {})
if associations.pop("*.rewind", None) is not None:
    if not associations:
        del settings[key]
    with open(path, "w") as f:
        json.dump(settings, f, indent=4)
        f.write("\n")
    print(f"Updated {path}")
PYEOF
