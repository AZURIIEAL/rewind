#!/bin/sh
# Reverts register-rewind-icon.sh — removes the MIME type registration and icons.
#
# Usage: ./unregister-rewind-icon.sh

set -e

MIME_PACKAGE_DIR="$HOME/.local/share/mime/packages"
ICON_THEME_DIR="$HOME/.local/share/icons/hicolor"
ICON_NAME="application-x-rewind-snapshot"

rm -f "$MIME_PACKAGE_DIR/rewind-snapshot.xml"

for size in 16 32 48 64 128 256 512; do
    rm -f "$ICON_THEME_DIR/${size}x${size}/mimetypes/${ICON_NAME}.png"
done

update-mime-database "$HOME/.local/share/mime" >/dev/null
gtk-update-icon-cache -f -t "$ICON_THEME_DIR" >/dev/null 2>&1 || true

if command -v nautilus >/dev/null 2>&1; then
    nautilus -q >/dev/null 2>&1 || true
fi

echo "Removed .rewind icon registration."
