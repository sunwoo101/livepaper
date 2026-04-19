#!/bin/bash
set -e

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PUBLISH_DIR="$ROOT/publish"
APPDIR="$ROOT/AppDir"
ICON="$ROOT/src/livepaper/Assets/livepaper.png"
APPIMAGETOOL="$ROOT/scripts/appimagetool"
OUTPUT="$ROOT/livepaper-x86_64.AppImage"

echo "==> Building livepaper..."
dotnet publish "$ROOT/src/livepaper" \
    -r linux-x64 \
    --self-contained \
    -c Release \
    -p:PublishSingleFile=true \
    -o "$PUBLISH_DIR"

echo "==> Setting up AppDir..."
rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin"
install -m 755 "$PUBLISH_DIR/livepaper" "$APPDIR/usr/bin/livepaper"

cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/bash
exec "$APPDIR/usr/bin/livepaper" "$@"
EOF
chmod +x "$APPDIR/AppRun"

cat > "$APPDIR/livepaper.desktop" <<'EOF'
[Desktop Entry]
Name=Livepaper
Comment=Live wallpaper manager for Wayland
Exec=livepaper
Icon=livepaper
Type=Application
Categories=Utility;
Keywords=wallpaper;live;wayland;video;
EOF

cp "$ICON" "$APPDIR/livepaper.png"

echo "==> Fetching appimagetool..."
if ! command -v appimagetool &>/dev/null && [ ! -f "$APPIMAGETOOL" ]; then
    wget -q --show-progress \
        "https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage" \
        -O "$APPIMAGETOOL"
    chmod +x "$APPIMAGETOOL"
fi

TOOL=$(command -v appimagetool 2>/dev/null || echo "$APPIMAGETOOL")

echo "==> Packaging AppImage..."
ARCH=x86_64 "$TOOL" "$APPDIR" "$OUTPUT"

echo ""
echo "Done: $OUTPUT"
