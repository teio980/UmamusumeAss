#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
DIST_DIR="$ROOT_DIR/dist"
VERSION="$(cat "$ROOT_DIR/version.txt" | tr -d '[:space:]')"

echo "=== Packaging Umamusume Assistant v$VERSION ==="
mkdir -p "$DIST_DIR"

# 1. Publish Umamusume.Avalonia for osx-arm64
echo "Publishing Avalonia for osx-arm64..."
DOTNET_OSX_DIR="$ROOT_DIR/build/publish/avalonia-osx-arm64"
rm -rf "$DOTNET_OSX_DIR"
dotnet publish "$ROOT_DIR/src/Umamusume.Avalonia/Umamusume.Avalonia.csproj" \
    -c Release \
    -r osx-arm64 \
    --self-contained true \
    -o "$DOTNET_OSX_DIR"

# Build macOS .app structure
APP_BUNDLE="$DIST_DIR/UmamusumeAss.app"
rm -rf "$APP_BUNDLE"
mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Frameworks"
mkdir -p "$APP_BUNDLE/Contents/Resources"

cp -R "$DOTNET_OSX_DIR/"* "$APP_BUNDLE/Contents/MacOS/"

# Copy native Core dylib and workers
DYLIB_FOUND=""
if [ -f "$ROOT_DIR/build/osx-arm64-debug/src/UmaAssistantCore/UmamusumeCore.dylib" ]; then
    DYLIB_FOUND="$ROOT_DIR/build/osx-arm64-debug/src/UmaAssistantCore/UmamusumeCore.dylib"
elif [ -f "$ROOT_DIR/build/osx-arm64/src/UmaAssistantCore/UmamusumeCore.dylib" ]; then
    DYLIB_FOUND="$ROOT_DIR/build/osx-arm64/src/UmaAssistantCore/UmamusumeCore.dylib"
elif [ -f "$ROOT_DIR/build/test-cmake/src/UmaAssistantCore/UmamusumeCore.dylib" ]; then
    DYLIB_FOUND="$ROOT_DIR/build/test-cmake/src/UmaAssistantCore/UmamusumeCore.dylib"
fi

if [ -n "$DYLIB_FOUND" ]; then
    cp "$DYLIB_FOUND" "$APP_BUNDLE/Contents/MacOS/UmamusumeCore.dylib"
    cp "$DYLIB_FOUND" "$APP_BUNDLE/Contents/Frameworks/UmamusumeCore.dylib"
fi

WORKER_FOUND=""
if [ -f "$ROOT_DIR/build/osx-arm64-debug/src/UmaAssistantCore/UmaNumericOcrWorker" ]; then
    WORKER_FOUND="$ROOT_DIR/build/osx-arm64-debug/src/UmaAssistantCore/UmaNumericOcrWorker"
elif [ -f "$ROOT_DIR/build/osx-arm64/src/UmaAssistantCore/UmaNumericOcrWorker" ]; then
    WORKER_FOUND="$ROOT_DIR/build/osx-arm64/src/UmaAssistantCore/UmaNumericOcrWorker"
elif [ -f "$ROOT_DIR/build/test-cmake/src/UmaAssistantCore/UmaNumericOcrWorker" ]; then
    WORKER_FOUND="$ROOT_DIR/build/test-cmake/src/UmaAssistantCore/UmaNumericOcrWorker"
fi

if [ -n "$WORKER_FOUND" ]; then
    cp "$WORKER_FOUND" "$APP_BUNDLE/Contents/MacOS/UmaNumericOcrWorker"
    cp "$WORKER_FOUND" "$APP_BUNDLE/Contents/Frameworks/UmaNumericOcrWorker"
fi

# Copy resource directory
cp -R "$ROOT_DIR/resource" "$APP_BUNDLE/Contents/Resources/"
cp -R "$ROOT_DIR/resource" "$APP_BUNDLE/Contents/MacOS/"

# Create Info.plist
cat > "$APP_BUNDLE/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>en</string>
    <key>CFBundleExecutable</key>
    <string>Umamusume.Avalonia</string>
    <key>CFBundleIdentifier</key>
    <string>com.umamusumeass.avalonia</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>UmamusumeAss</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleVersion</key>
    <string>$VERSION</string>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
EOF

chmod +x "$APP_BUNDLE/Contents/MacOS/Umamusume.Avalonia"
if [ -f "$APP_BUNDLE/Contents/MacOS/UmaNumericOcrWorker" ]; then
    chmod +x "$APP_BUNDLE/Contents/MacOS/UmaNumericOcrWorker"
fi
if [ -f "$APP_BUNDLE/Contents/Frameworks/UmaNumericOcrWorker" ]; then
    chmod +x "$APP_BUNDLE/Contents/Frameworks/UmaNumericOcrWorker"
fi

echo "Applying ad-hoc codesign to .app..."
codesign --force --deep -s - "$APP_BUNDLE"

# Zip macOS .app
echo "Creating macOS .app ZIP..."
rm -f "$DIST_DIR/UmamusumeAss-$VERSION-osx-arm64.app.zip"
(cd "$DIST_DIR" && zip -rq "UmamusumeAss-$VERSION-osx-arm64.app.zip" "UmamusumeAss.app")

echo "Note: Windows distribution is built with the original WPF client (UmamusumeWpfGui) via tools/package.ps1."

echo "=== Packaging Complete ==="
ls -lh "$DIST_DIR"/*.zip
