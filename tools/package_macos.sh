#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

echo "=== Packaging Umamusume Assistant for macOS ARM64 ==="

DEFAULT_BUILD_DIR="$REPO_ROOT/build/osx-arm64-debug"
if [ ! -d "$DEFAULT_BUILD_DIR" ]; then
    DEFAULT_BUILD_DIR="$REPO_ROOT/build/test-cmake"
fi
BUILD_DIR="${1:-$DEFAULT_BUILD_DIR}"
DIST_DIR="${2:-$REPO_ROOT/dist}"
APP_NAME="UmamusumeAss.app"
APP_DIR="$DIST_DIR/$APP_NAME"
TEMP_PUBLISH="$REPO_ROOT/build/temp_publish_avalonia"
TEMP_HELPER="$REPO_ROOT/build/temp_publish_mac_updater"
VERSION="$(tr -d '[:space:]' < "$REPO_ROOT/version.txt")"
BUNDLED_RESOURCE_VERSION="${UMA_BUNDLED_RESOURCE_VERSION:-$VERSION}"

mkdir -p "$DIST_DIR"
rm -rf "$APP_DIR" "$TEMP_PUBLISH" "$TEMP_HELPER"
mkdir -p "$APP_DIR/Contents/MacOS"
mkdir -p "$APP_DIR/Contents/Frameworks"
mkdir -p "$APP_DIR/Contents/Resources"
mkdir -p "$APP_DIR/Contents/Helpers"

echo "1. Publishing .NET Avalonia application..."
dotnet publish "$REPO_ROOT/src/Umamusume.Avalonia/Umamusume.Avalonia.csproj" \
    -c Release \
    -r osx-arm64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    --disable-build-servers \
    -p:UseSharedCompilation=false \
    -o "$TEMP_PUBLISH"

echo "2. Populating Contents/MacOS..."
find "$TEMP_PUBLISH" -mindepth 1 -maxdepth 1 ! -name '*.pdb' -exec cp -R {} "$APP_DIR/Contents/MacOS/" \;
dotnet publish "$REPO_ROOT/src/Umamusume.MacUpdater/Umamusume.MacUpdater.csproj" \
    -c Release -r osx-arm64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    --disable-build-servers -p:UseSharedCompilation=false -o "$TEMP_HELPER"
cp "$TEMP_HELPER/Umamusume.MacUpdater" "$APP_DIR/Contents/Helpers/Umamusume.MacUpdater"
chmod +x "$APP_DIR/Contents/Helpers/Umamusume.MacUpdater"
cp "$REPO_ROOT/version.txt" "$APP_DIR/Contents/Resources/version.txt"
printf '%s\n' "$BUNDLED_RESOURCE_VERSION" > "$APP_DIR/Contents/Resources/resource-version.txt"
chmod +x "$APP_DIR/Contents/MacOS/Umamusume.Avalonia"

echo "3. Copying native UmamusumeCore.dylib & worker..."
DYLIB_PATH="$BUILD_DIR/src/UmaAssistantCore/UmamusumeCore.dylib"
if [ ! -f "$DYLIB_PATH" ]; then
    echo "Error: Native library not found at $DYLIB_PATH" >&2
    exit 1
fi
cp "$DYLIB_PATH" "$APP_DIR/Contents/Frameworks/UmamusumeCore.dylib"
cp "$DYLIB_PATH" "$APP_DIR/Contents/MacOS/UmamusumeCore.dylib"

WORKER_PATH="$BUILD_DIR/src/UmaAssistantCore/UmaNumericOcrWorker"
if [ -f "$WORKER_PATH" ]; then
    cp "$WORKER_PATH" "$APP_DIR/Contents/MacOS/UmaNumericOcrWorker"
    cp "$WORKER_PATH" "$APP_DIR/Contents/Frameworks/UmaNumericOcrWorker"
    chmod +x "$APP_DIR/Contents/MacOS/UmaNumericOcrWorker"
    chmod +x "$APP_DIR/Contents/Frameworks/UmaNumericOcrWorker"
fi

REPLAY_PATH="$BUILD_DIR/tools/uma_pipeline_replay"
if [ -f "$REPLAY_PATH" ]; then
    cp "$REPLAY_PATH" "$APP_DIR/Contents/MacOS/uma_pipeline_replay"
    chmod +x "$APP_DIR/Contents/MacOS/uma_pipeline_replay"
else
    echo "Error: Native pipeline replay/inspector not found at $REPLAY_PATH" >&2
    exit 1
fi

echo "4. Copying bundled resources & tessdata..."
cp -R "$REPO_ROOT/resource" "$APP_DIR/Contents/Resources/resource"
# Verify eng.traineddata is present
if [ ! -f "$APP_DIR/Contents/Resources/resource/tessdata/eng.traineddata" ]; then
    echo "Error: eng.traineddata missing from bundled resources!" >&2
    exit 1
fi

echo "5. Creating application icon and Info.plist..."
ICONSET_DIR="$TEMP_PUBLISH/AppIcon.iconset"
mkdir -p "$ICONSET_DIR"
for icon_size in 16 32 128 256 512; do
    sips -z "$icon_size" "$icon_size" "$REPO_ROOT/src/UmamusumeWpfGui/Assets/teio-logo.png" --out "$ICONSET_DIR/icon_${icon_size}x${icon_size}.png" >/dev/null
    retina_size=$((icon_size * 2))
    sips -z "$retina_size" "$retina_size" "$REPO_ROOT/src/UmamusumeWpfGui/Assets/teio-logo.png" --out "$ICONSET_DIR/icon_${icon_size}x${icon_size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET_DIR" -o "$APP_DIR/Contents/Resources/AppIcon.icns"

VERSION="$(tr -d '[:space:]' < "$REPO_ROOT/version.txt")"
cat << EOF > "$APP_DIR/Contents/Info.plist"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>Umamusume.Avalonia</string>
    <key>CFBundleIconFile</key>
    <string>AppIcon</string>
    <key>CFBundleIdentifier</key>
    <string>com.umamusume.assistant</string>
    <key>CFBundleName</key>
    <string>UmamusumeAss</string>
    <key>CFBundleDisplayName</key>
    <string>Umamusume Assistant</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleVersion</key>
    <string>$VERSION</string>
    <key>LSMinimumSystemVersion</key>
    <string>14.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSSupportsAutomaticGraphicsSwitching</key>
    <true/>
</dict>
</plist>
EOF

echo -n "APPL????" > "$APP_DIR/Contents/PkgInfo"

echo "6. Signing Mach-O files inside-out, then sealing the app bundle..."
while IFS= read -r -d '' BINARY_PATH; do
    if file -b "$BINARY_PATH" | grep -q 'Mach-O'; then
        codesign --force --sign - "$BINARY_PATH"
    fi
done < <(find "$APP_DIR/Contents" -type f -print0)
codesign --force --sign - "$APP_DIR"
codesign --verify --deep --strict "$APP_DIR"

echo "7. Creating .app ZIP distribution..."
(cd "$DIST_DIR" && rm -f "UmamusumeAss-osx-arm64.app.zip" "UmamusumeAss-v$VERSION-osx-arm64.app.zip" && \
 zip -q -r -y "UmamusumeAss-v$VERSION-osx-arm64.app.zip" "$APP_NAME" && \
 cp "UmamusumeAss-v$VERSION-osx-arm64.app.zip" "UmamusumeAss-osx-arm64.app.zip")

echo "=== Packaging complete: $DIST_DIR/UmamusumeAss-v$VERSION-osx-arm64.app.zip ==="
rm -rf "$TEMP_PUBLISH" "$TEMP_HELPER"
