#!/bin/zsh
# Builds the standalone Plancia preview as a small .app and opens it in the background
# (open -g: it never takes the keyboard focus). Nothing of AL-76 runs.
set -euo pipefail
ROOT="${0:A:h:h:h}"
APP="$ROOT/build/PlanciaPreview.app"
mkdir -p "$APP/Contents/MacOS"
xcrun swiftc -O -swift-version 5 -target arm64-apple-macosx14.0 \
    "$ROOT/tests/plancia/PlanciaPreview.swift" -o "$APP/Contents/MacOS/PlanciaPreview"
cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>PlanciaPreview</string>
<key>CFBundleIdentifier</key><string>local.al76.plancia-preview</string>
<key>CFBundleName</key><string>Anteprima Plancia</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>LSMinimumSystemVersion</key><string>14.0</string>
</dict></plist>
PLIST
[[ "${1:-}" == "--build-only" ]] || open -g "$APP"
