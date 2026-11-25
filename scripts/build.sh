#!/bin/zsh
# Builds AL-76 (formerly JARVIS) and creates build/AL-76.app.
#   ./scripts/build.sh            builds and assembles the app
#   ./scripts/build.sh --install  also copies it to /Applications and launches it
set -euo pipefail

ROOT="${0:A:h:h}"
APP="$ROOT/build/AL-76.app"
rm -rf "$ROOT/build/JARVIS.app"   # the name until 1.30.63
cd "$ROOT"
# This Mac's app bundle identifier (local.env, outside Git; example in local.env.example).
[[ -f "$ROOT/local.env" ]] && source "$ROOT/local.env"
BUNDLE_ID="${JARVIS_BUNDLE_ID:-com.example.jarvis}"

echo "▸ Compilazione (release)…"
swift build -c release --arch arm64
BIN="$(swift build -c release --arch arm64 --show-bin-path)"

echo "▸ Icone…"
# Three app icons, chosen in Impostazioni › Aspetto (1.30.86): the robottino is the bundle's own
# (AppIcon.icns), the app puts the others on its bundle when chosen (Icona-<name>.icns).
ICON_DIR="$ROOT/build/icon"
if [[ ! -f "$ICON_DIR/robottino.icns" || -n "$(find "$ROOT/scripts/icone" -type f -newer "$ICON_DIR/robottino.icns")" ]]; then
    rm -rf "$ICON_DIR" && mkdir -p "$ICON_DIR"
    swiftc -O "$ROOT"/scripts/icone/*.swift -o "$ICON_DIR/make_icon"
    for name in rete reattore robottino; do
        mkdir -p "$ICON_DIR/$name.iconset"
        "$ICON_DIR/make_icon" "$name" "$ICON_DIR/$name.png"
        for s in 16 32 128 256 512; do
            sips -z $s $s "$ICON_DIR/$name.png" --out "$ICON_DIR/$name.iconset/icon_${s}x${s}.png" >/dev/null
            sips -z $((s*2)) $((s*2)) "$ICON_DIR/$name.png" --out "$ICON_DIR/$name.iconset/icon_${s}x${s}@2x.png" >/dev/null
        done
        iconutil -c icns "$ICON_DIR/$name.iconset" -o "$ICON_DIR/$name.icns"
    done
fi

echo "▸ Pacchetto .app…"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources/porcupine"
cp "$BIN/JARVIS" "$APP/Contents/MacOS/JARVIS"
cp "$ROOT/Resources/Info.plist" "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleIdentifier $BUNDLE_ID" "$APP/Contents/Info.plist"
cp "$ICON_DIR/robottino.icns" "$APP/Contents/Resources/AppIcon.icns"
for name in rete reattore robottino; do cp "$ICON_DIR/$name.icns" "$APP/Contents/Resources/Icona-$name.icns"; done
cp "$ROOT"/Vendor/porcupine/{libpv_porcupine.dylib,porcupine_params.pv,jarvis_mac.ppn} "$APP/Contents/Resources/porcupine/"
cp "$ROOT/Resources/tts_server.py" "$APP/Contents/Resources/tts_server.py"
cp "$ROOT/Resources/voce_jarvis.wav" "$APP/Contents/Resources/voce_jarvis.wav"
cp "$ROOT/Resources/voce_robottino.wav" "$APP/Contents/Resources/voce_robottino.wav"
cp "$ROOT/Resources/ros_bridge.py" "$APP/Contents/Resources/ros_bridge.py"

echo "▸ Firma…"
# A stable signature lets macOS remember permissions (microphone, Mail…) between builds.
# Without a valid certificate the signature would be "ad hoc", different at every build, and macOS
# would ask for every permission again: better to stop and say so.
IDENTITY=$(security find-identity -v -p codesigning | awk -F'"' '/Apple Development/ {print $2; exit}')
if [[ -z "$IDENTITY" ]]; then
    if [[ "${JARVIS_ADHOC_SIGN:-}" == "1" ]]; then
        IDENTITY="-"
        echo "  ⚠︎ firma ad hoc: macOS chiederà di nuovo i permessi"
    else
        echo "✗ Certificato Apple Development non trovato o scaduto (Xcode › Impostazioni › Account)." >&2
        echo "  Senza, macOS richiederebbe tutti i permessi. Per firmare comunque: JARVIS_ADHOC_SIGN=1 $0 $*" >&2
        exit 1
    fi
fi
codesign --force --sign "$IDENTITY" "$APP/Contents/Resources/porcupine/libpv_porcupine.dylib"
codesign --force --sign "$IDENTITY" --entitlements "$ROOT/Resources/JARVIS.entitlements" "$APP"
echo "  firmata con: $IDENTITY ($BUNDLE_ID)"

if [[ "${1:-}" == "--install" ]]; then
    echo "▸ Installazione in /Applications…"
    osascript -e "tell application id \"$BUNDLE_ID\" to quit" 2>/dev/null || true
    sleep 1
    # Until 1.30.63 the app was /Applications/JARVIS.app: same bundle id and signature, so the permissions stay.
    rm -rf /Applications/JARVIS.app /Applications/AL-76.app
    cp -R "$APP" /Applications/AL-76.app
    open /Applications/AL-76.app
fi
echo "✓ Pronto: $APP"
