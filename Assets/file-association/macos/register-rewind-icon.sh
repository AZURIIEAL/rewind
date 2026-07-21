#!/bin/sh
# Registers a Finder icon for .rewind files.
#
# macOS ties file icons to Launch Services registrations, not a plain extension
# map, so this builds a minimal (non-executable, never launched) app bundle
# whose only job is to declare ownership of the .rewind file type and its icon,
# then registers that bundle with Launch Services. Run this on macOS itself —
# it will not work from Linux/WSL.
#
# Usage: ./register-rewind-icon.sh

set -e

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
ICONS_DIR="$SCRIPT_DIR/../../icons"
SOURCE_PNG="$ICONS_DIR/file-icon-512.png"
APP_NAME="RewindFileIcon.app"
APP_PATH="$HOME/Applications/$APP_NAME"
ICONSET_DIR=$(mktemp -d)/RewindFile.iconset

if ! command -v sips >/dev/null 2>&1 || ! command -v iconutil >/dev/null 2>&1; then
    echo "This script requires sips and iconutil, which are macOS-only tools." >&2
    exit 1
fi

if [ ! -f "$SOURCE_PNG" ]; then
    echo "Could not find $SOURCE_PNG" >&2
    exit 1
fi

mkdir -p "$ICONSET_DIR"
mkdir -p "$HOME/Applications"

for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$SOURCE_PNG" --out "$ICONSET_DIR/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z "$double" "$double" "$SOURCE_PNG" --out "$ICONSET_DIR/icon_${size}x${size}@2x.png" >/dev/null
done

ICNS_PATH=$(mktemp -d)/RewindFile.icns
iconutil -c icns "$ICONSET_DIR" -o "$ICNS_PATH"

mkdir -p "$APP_PATH/Contents/MacOS"
mkdir -p "$APP_PATH/Contents/Resources"
cp "$ICNS_PATH" "$APP_PATH/Contents/Resources/RewindFile.icns"

cat > "$APP_PATH/Contents/MacOS/RewindFileIcon" <<'EOF'
#!/bin/sh
# Placeholder executable so this is a valid app bundle. Never meant to be launched.
exit 0
EOF
chmod +x "$APP_PATH/Contents/MacOS/RewindFileIcon"

cat > "$APP_PATH/Contents/Info.plist" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>RewindFileIcon</string>
    <key>CFBundleIdentifier</key>
    <string>dev.rewind.fileicon</string>
    <key>CFBundleVersion</key>
    <string>1.0</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleExecutable</key>
    <string>RewindFileIcon</string>
    <key>CFBundleIconFile</key>
    <string>RewindFile</string>
    <key>LSUIElement</key>
    <true/>
    <key>CFBundleDocumentTypes</key>
    <array>
        <dict>
            <key>CFBundleTypeName</key>
            <string>Rewind Snapshot</string>
            <key>CFBundleTypeExtensions</key>
            <array>
                <string>rewind</string>
            </array>
            <key>CFBundleTypeIconFile</key>
            <string>RewindFile</string>
            <key>CFBundleTypeRole</key>
            <string>Viewer</string>
            <key>LSHandlerRank</key>
            <string>Owner</string>
        </dict>
    </array>
</dict>
</plist>
EOF

/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$APP_PATH"

touch /tmp/rewind-icon-refresh.rewind
killall Finder >/dev/null 2>&1 || true
rm -f /tmp/rewind-icon-refresh.rewind

echo "Registered .rewind Finder icon via $APP_PATH"
echo "If the icon doesn't show immediately, log out and back in (Launch Services caches icons per-session)."
