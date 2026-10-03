#!/bin/sh
# Installs (or updates) BassRouter for the current user.
# Run this from an extracted release folder, or from a `dotnet publish` output folder
# that also contains bassrouter.desktop and bassrouter.png.
#
# Usage: ./install.sh [--uninstall]
set -eu

SOURCE_DIR="$(cd "$(dirname "$0")" && pwd)"
DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
INSTALL_DIR="$DATA_HOME/bassrouter"
APPLICATIONS_DIR="$DATA_HOME/applications"
ICON_DIR="$DATA_HOME/icons/hicolor/512x512/apps"
BIN_DIR="$HOME/.local/bin"

if [ "${1:-}" = "--uninstall" ]; then
    rm -rf "$INSTALL_DIR"
    rm -f "$BIN_DIR/bassrouter" "$APPLICATIONS_DIR/bassrouter.desktop" "$ICON_DIR/bassrouter.png"
    echo "BassRouter uninstalled."
    exit 0
fi

if [ ! -f "$SOURCE_DIR/BassRouter" ]; then
    echo "BassRouter executable not found next to this script." >&2
    exit 1
fi

if [ "$SOURCE_DIR" = "$INSTALL_DIR" ]; then
    echo "Run this script from the extracted release folder, not the installed copy." >&2
    exit 1
fi

mkdir -p "$APPLICATIONS_DIR" "$ICON_DIR" "$BIN_DIR"

# Replace the previous install entirely so stale files don't linger
rm -rf "$INSTALL_DIR"
mkdir -p "$INSTALL_DIR"
cp -R "$SOURCE_DIR"/. "$INSTALL_DIR"/
chmod +x "$INSTALL_DIR/BassRouter"

ln -sf "$INSTALL_DIR/BassRouter" "$BIN_DIR/bassrouter"
cp "$SOURCE_DIR/bassrouter.png" "$ICON_DIR/bassrouter.png"
# Escape characters that are special in a sed replacement
EXEC_PATH=$(printf '%s' "$INSTALL_DIR/BassRouter" | sed 's/[\\|&]/\\&/g')
sed "s|^Exec=.*|Exec=\"$EXEC_PATH\"|" "$SOURCE_DIR/bassrouter.desktop" > "$APPLICATIONS_DIR/bassrouter.desktop"

update-desktop-database "$APPLICATIONS_DIR" >/dev/null 2>&1 || true
gtk-update-icon-cache "$DATA_HOME/icons/hicolor" >/dev/null 2>&1 || true

echo "BassRouter installed to $INSTALL_DIR"
echo "Launch it from your app menu, or run 'bassrouter'."
