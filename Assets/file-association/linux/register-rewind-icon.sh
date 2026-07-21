#!/bin/sh
# Registers a file-manager icon for .rewind files on Linux desktops (GNOME/Nautilus,
# KDE/Dolphin, etc.), using the standard freedesktop.org shared-mime-info + icon
# theme mechanism. Per-user only (~/.local/share) — no root required.
#
# Usage: ./register-rewind-icon.sh

set -e

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
ICONS_DIR="$SCRIPT_DIR/../../icons"
MIME_TYPE="application/x-rewind-snapshot"
ICON_NAME="application-x-rewind-snapshot"

MIME_PACKAGE_DIR="$HOME/.local/share/mime/packages"
ICON_THEME_DIR="$HOME/.local/share/icons/hicolor"

mkdir -p "$MIME_PACKAGE_DIR"

cat > "$MIME_PACKAGE_DIR/rewind-snapshot.xml" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
  <mime-type type="$MIME_TYPE">
    <comment>Rewind Snapshot</comment>
    <glob pattern="*.rewind"/>
  </mime-type>
</mime-info>
EOF

for size in 16 32 48 64 128 256 512; do
    src="$ICONS_DIR/file-icon-${size}.png"
    if [ -f "$src" ]; then
        dest_dir="$ICON_THEME_DIR/${size}x${size}/mimetypes"
        mkdir -p "$dest_dir"
        cp "$src" "$dest_dir/${ICON_NAME}.png"
    fi
done

update-mime-database "$HOME/.local/share/mime" >/dev/null
gtk-update-icon-cache -f -t "$ICON_THEME_DIR" >/dev/null 2>&1 || true

# Nautilus caches icons per-process; restart it so the change shows up immediately.
if command -v nautilus >/dev/null 2>&1; then
    nautilus -q >/dev/null 2>&1 || true
fi

echo "Registered .rewind icon as MIME type $MIME_TYPE."
echo "Run unregister-rewind-icon.sh to revert."
