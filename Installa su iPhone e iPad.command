#!/bin/zsh
# Double-click in the Finder: builds and installs the JARVIS app on iPhones and iPads connected by cable.
# The same can be done from Xcode, opening iPhone/JARVISPhone.xcodeproj and pressing Run.
cd "${0:A:h}" || exit 1
PROJECT=iPhone/JARVISPhone.xcodeproj
# Apple Team and app bundle identifier: in local.env, outside Git (example in local.env.example).
[[ -f local.env ]] && source ./local.env
BUILD="$HOME/Library/Caches/JARVISPhone"
finish() {
    echo
    echo "$1"
    [[ -t 0 && -z "$JARVIS_NO_WAIT" ]] && { echo "Premi Invio per chiudere."; read -r; }
    exit "${2:-0}"
}

command -v xcodebuild >/dev/null && xcrun --find devicectl >/dev/null 2>&1 || finish "Manca Xcode: installalo dall'App Store e aprilo una volta." 1
[[ -n "$JARVIS_TEAM" ]] || finish "Manca il Team ID del tuo account Apple: copia local.env.example in local.env e scrivilo in JARVIS_TEAM." 1
SIGNING=(DEVELOPMENT_TEAM="$JARVIS_TEAM" PRODUCT_BUNDLE_IDENTIFIER="${JARVIS_IPHONE_BUNDLE_ID:-com.example.jarvis.iphone}")

LIST=$(mktemp)
xcrun devicectl list devices --json-output "$LIST" >/dev/null 2>&1
DEVICES=("${(@f)$(/usr/bin/python3 -I - "$LIST" <<'PY'
import json, sys
devices = json.load(open(sys.argv[1]))["result"]["devices"]
for d in devices:
    h = d.get("hardwareProperties", {})
    if h.get("reality") == "physical" and h.get("deviceType") in ("iPhone", "iPad") and d.get("connectionProperties", {}).get("tunnelState") != "unavailable":
        print(h.get("udid", ""))
PY
)}")
rm -f "$LIST"
DEVICES=(${DEVICES:#})
if [[ ${#DEVICES} -eq 0 && "$1" != "--solo-compila" ]]; then
    finish "Non vedo né iPhone né iPad. Collegane uno col cavo, sbloccalo, tocca Autorizza se te lo chiede, e riprova." 1
fi

# Xcode reuses the app's profile until it expires, so a reinstall did not move the 7-day deadline (12 November).
# Profiles of this app with less than 6 days left are moved aside: Xcode then makes a new one, valid 7 days.
PROFILES="$HOME/Library/Developer/Xcode/UserData/Provisioning Profiles"
OLD="$HOME/Library/Developer/Xcode/UserData/profili-scaduti-jarvis"
for PROFILE in "$PROFILES"/*.mobileprovision(N); do
    INFO=$(security cms -D -i "$PROFILE" 2>/dev/null) || continue
    [[ "$INFO" == *".${JARVIS_IPHONE_BUNDLE_ID:-com.example.jarvis.iphone}<"* ]] || continue
    LEFT=$(/usr/bin/python3 -I -c 'import plistlib,sys,datetime; d=plistlib.loads(sys.stdin.buffer.read())["ExpirationDate"]; print(int((d.replace(tzinfo=datetime.timezone.utc)-datetime.datetime.now(datetime.timezone.utc)).total_seconds()//86400))' <<<"$INFO" 2>/dev/null)
    if [[ -n "$LEFT" && "$LEFT" -lt 6 ]]; then
        mkdir -p "$OLD" && mv "$PROFILE" "$OLD/" && echo "Rinnovo la firma (quella vecchia scadeva tra $LEFT giorni)."
    fi
done

echo "Compilo l'app, può volerci un minuto..."
LOG=$(mktemp)
if [[ "$1" == "--solo-compila" ]]; then
    TARGETS=("generic/platform=iOS")
else
    TARGETS=()
    for DEVICE in $DEVICES; do TARGETS+=("id=$DEVICE"); done
fi
# One build per device also registers it in the profile; the last one, which contains them all, is installed.
for TARGET in $TARGETS; do
    if ! xcodebuild -project "$PROJECT" -scheme JARVISPhone -configuration Release -destination "$TARGET" -allowProvisioningUpdates \
            -allowProvisioningDeviceRegistration -derivedDataPath "$BUILD" "${SIGNING[@]}" build >"$LOG" 2>&1; then
        grep -E "error:" "$LOG" | grep -v Auto-pair | sort -u | head -5
        if grep -q "Developer Mode" "$LOG"; then
            finish "Attiva la Modalità sviluppatore: Impostazioni › Privacy e sicurezza, in fondo." 1
        fi
        finish "Compilazione non riuscita. Se parla di account, apri Xcode › Settings › Apple Account e controlla di aver fatto l'accesso." 1
    fi
done
rm -f "$LOG"
APP="$BUILD/Build/Products/Release-iphoneos/JARVIS.app"
[[ "$1" == "--solo-compila" ]] && finish "Compilata: $APP"

for DEVICE in $DEVICES; do
    echo "Installo su $DEVICE..."
    xcrun devicectl device install app --device "$DEVICE" "$APP" >/dev/null || finish "Installazione non riuscita: sblocca il dispositivo e riprova." 1
done
UNTIL=$(security cms -D -i "$APP/embedded.mobileprovision" 2>/dev/null | /usr/bin/plutil -extract ExpirationDate raw -o - - 2>/dev/null | cut -c1-10)
finish "Fatto: installata${UNTIL:+, funziona fino al $UNTIL}. Abbinamento e impostazioni restano quelli di prima."
