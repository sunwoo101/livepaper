#!/bin/bash
set -e

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PUBLISH_DIR="$ROOT/publish"
ICON="$ROOT/src/livepaper/Assets/livepaper.png"

BIN_DIR="$HOME/.local/bin"
APPS_DIR="$HOME/.local/share/applications"
ICONS_DIR="$HOME/.local/share/icons/hicolor/512x512/apps"

echo "==> Building livepaper..."
dotnet publish "$ROOT/src/livepaper" \
    -r linux-x64 \
    --self-contained \
    -c Release \
    -o "$PUBLISH_DIR"

echo "==> Installing to $HOME/.local/lib/livepaper/..."
mkdir -p "$HOME/.local/lib/livepaper"
cp -r "$PUBLISH_DIR"/. "$HOME/.local/lib/livepaper/"
chmod 755 "$HOME/.local/lib/livepaper/livepaper"

echo "==> Installing launcher to $BIN_DIR..."
mkdir -p "$BIN_DIR"
cat > "$BIN_DIR/livepaper" <<'WRAPPER'
#!/bin/bash
exec "$HOME/.local/lib/livepaper/livepaper" "$@"
WRAPPER
chmod 755 "$BIN_DIR/livepaper"

echo "==> Installing icon..."
mkdir -p "$ICONS_DIR"
cp "$ICON" "$ICONS_DIR/livepaper.png"

echo "==> Installing desktop entry..."
mkdir -p "$APPS_DIR"
cat > "$APPS_DIR/livepaper.desktop" <<EOF
[Desktop Entry]
Name=Livepaper
Comment=Live wallpaper manager for Wayland
Exec=$BIN_DIR/livepaper
Icon=livepaper
Type=Application
Categories=Utility;
Keywords=wallpaper;live;wayland;video;
EOF

update-desktop-database "$APPS_DIR" 2>/dev/null || true

echo ""
echo "Done. Make sure $BIN_DIR is in your PATH."
echo "Run: livepaper"
