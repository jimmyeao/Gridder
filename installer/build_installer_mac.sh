#!/usr/bin/env bash
set -euo pipefail

# =============================================================
#  Gridder macOS Installer Build Script
#  Builds the .NET MAUI app + Python standalone + DMG installer
#
#  Prerequisites:
#    - .NET 10 SDK with maui-maccatalyst workload
#    - Python 3.12 venv at python/.venv with deps installed
#    - Xcode Command Line Tools
#    - Optional: create-dmg (brew install create-dmg) for styled DMG
#    - Optional: CODESIGN_IDENTITY env var for code signing
# =============================================================

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PYTHON_DIR="$ROOT/python"
INSTALLER_DIR="$ROOT/installer"
PUBLISH_DIR="$ROOT/publish/maccatalyst"

echo ""
echo "========================================"
echo "  Gridder macOS Build"
echo "========================================"
echo ""

# --- Step 1: Publish .NET MAUI app ---
echo "[1/4] Publishing .NET MAUI app..."
rm -rf "$PUBLISH_DIR"

dotnet publish "$ROOT/Gridder/Gridder.csproj" \
    -f net10.0-maccatalyst \
    -c Release \
    --self-contained true \
    -o "$PUBLISH_DIR" \
    -p:CreatePackage=false \
    -p:EnableCodeSigning=false

echo ".NET publish complete."
echo ""

# Find the .app bundle in publish output
APP_BUNDLE=$(find "$PUBLISH_DIR" -name "*.app" -type d | head -1)
if [ -z "$APP_BUNDLE" ]; then
    echo "ERROR: Could not find .app bundle in $PUBLISH_DIR"
    exit 1
fi
APP_NAME=$(basename "$APP_BUNDLE")
echo "Found app bundle: $APP_NAME"

# --- Step 2: Build Python standalone exe ---
echo "[2/4] Building Python standalone exe..."
if [ ! -f "$PYTHON_DIR/.venv/bin/pyinstaller" ]; then
    echo "ERROR: PyInstaller not found. Run: python/.venv/bin/pip install pyinstaller"
    exit 1
fi

pushd "$PYTHON_DIR" > /dev/null
.venv/bin/pyinstaller gridder_analysis.spec --noconfirm
popd > /dev/null
echo "Python standalone build complete."
echo ""

# --- Step 3: Embed Python engine in .app bundle ---
echo "[3/4] Embedding Python engine in app bundle..."
RESOURCES_DIR="$APP_BUNDLE/Contents/Resources"
mkdir -p "$RESOURCES_DIR"

cp -R "$PYTHON_DIR/dist/gridder_analysis" "$RESOURCES_DIR/gridder_analysis"

# Make the binary executable
chmod +x "$RESOURCES_DIR/gridder_analysis/gridder_analysis"

echo "Python engine embedded."
echo ""

# --- Step 4: Code sign and create DMG ---
echo "[4/4] Code signing and creating DMG..."

# Code sign the app bundle
IDENTITY="${CODESIGN_IDENTITY:--}"
echo "Signing with identity: $IDENTITY"
codesign --force --deep --sign "$IDENTITY" "$APP_BUNDLE"

# Create DMG
DMG_OUTPUT="$INSTALLER_DIR/Output"
mkdir -p "$DMG_OUTPUT"
DMG_PATH="$DMG_OUTPUT/Gridder.dmg"
rm -f "$DMG_PATH"

if command -v create-dmg &> /dev/null; then
    # Styled DMG with create-dmg
    create-dmg \
        --volname "Gridder" \
        --window-pos 200 120 \
        --window-size 600 400 \
        --icon-size 100 \
        --icon "$APP_NAME" 150 190 \
        --app-drop-link 450 190 \
        --no-internet-enable \
        "$DMG_PATH" \
        "$APP_BUNDLE" || true

    # create-dmg returns non-zero if signing fails but DMG is still created
    if [ ! -f "$DMG_PATH" ]; then
        echo "create-dmg failed, falling back to hdiutil..."
        hdiutil create -volname "Gridder" -srcfolder "$APP_BUNDLE" \
            -ov -format UDZO "$DMG_PATH"
    fi
else
    # Fallback: plain DMG with hdiutil
    echo "create-dmg not found, using hdiutil..."
    hdiutil create -volname "Gridder" -srcfolder "$APP_BUNDLE" \
        -ov -format UDZO "$DMG_PATH"
fi

echo ""
echo "========================================"
echo "  BUILD COMPLETE"
echo "========================================"
echo "  App bundle: $APP_BUNDLE"
echo "  DMG:        $DMG_PATH"
echo "========================================"
