#!/bin/bash
set -euo pipefail

if [ "$#" -ne 1 ] || [ ! -s "$1" ]; then
    echo "Usage: $0 <UmamusumeAss-osx-arm64.app.zip>" >&2
    exit 2
fi

PACKAGE_PATH="$1"
VERIFY_ROOT="$(mktemp -d)"
trap 'rm -rf "$VERIFY_ROOT"' EXIT
unzip -q "$PACKAGE_PATH" -d "$VERIFY_ROOT"

APP_PATH="$VERIFY_ROOT/UmamusumeAss.app"
MAIN_EXECUTABLE="$APP_PATH/Contents/MacOS/Umamusume.Avalonia"
UPDATE_HELPER="$APP_PATH/Contents/Helpers/Umamusume.MacUpdater"
if [ ! -d "$APP_PATH" ] || [ ! -x "$MAIN_EXECUTABLE" ] || [ ! -x "$UPDATE_HELPER" ]; then
    echo "The app bundle or one of its required executables is missing or not executable." >&2
    exit 1
fi
while IFS= read -r -d '' CONTENT_PATH; do
    if ! file -b "$CONTENT_PATH" | grep -q 'Mach-O'; then
        echo "Only Mach-O files may be stored in Contents/MacOS: $CONTENT_PATH" >&2
        exit 1
    fi
done < <(find "$APP_PATH/Contents/MacOS" -type f -print0)

MINIMUM_SYSTEM_VERSION="$(/usr/libexec/PlistBuddy -c 'Print :LSMinimumSystemVersion' "$APP_PATH/Contents/Info.plist")"
if [ "$MINIMUM_SYSTEM_VERSION" != "14.0" ]; then
    echo "Expected LSMinimumSystemVersion 14.0, got $MINIMUM_SYSTEM_VERSION." >&2
    exit 1
fi
codesign --verify --deep --strict "$APP_PATH"

while IFS= read -r -d '' BINARY_PATH; do
    if ! file -b "$BINARY_PATH" | grep -q 'Mach-O'; then
        continue
    fi
    MINOS="$(xcrun vtool -show-build "$BINARY_PATH" | awk '$1 == "minos" { print $2; exit }')"
    if [ -z "$MINOS" ] || ! awk -v version="$MINOS" 'BEGIN { split(version, part, /[.]/); if (part[1] > 14 || (part[1] == 14 && (part[2] > 0 || part[3] > 0))) exit 1 }'; then
        echo "Mach-O has an invalid or newer-than-macOS-14 deployment target: $BINARY_PATH ($MINOS)." >&2
        exit 1
    fi
done < <(find "$APP_PATH" -type f -print0)

echo "Verified signed macOS app package: $PACKAGE_PATH"
