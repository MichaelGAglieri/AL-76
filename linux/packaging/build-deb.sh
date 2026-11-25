#!/bin/bash
# Makes al76_<version>_<arch>.deb from a published app folder. Runs on Linux (dpkg-deb), e.g. inside OrbStack:
#   packaging/build-deb.sh out/linux-arm64 arm64 0.0.1 out/
set -euo pipefail
app=${1:?cartella dell\'app}; arch=${2:?amd64|arm64}; version=${3:?versione}; dest=${4:-.}
here=$(cd "$(dirname "$0")" && pwd)
root=$(mktemp -d); trap 'rm -rf "$root"' EXIT
mkdir -p "$root/DEBIAN" "$root/opt/al76" "$root/usr/bin" "$root/usr/share/applications" "$root/usr/share/doc/al76"
cp -a "$app/." "$root/opt/al76/"
rm -f "$root/opt/al76/dev.flag" "$root/opt/al76/"*.pdb
ln -s /opt/al76/al76 "$root/usr/bin/al76"
install -m 644 "$here/al76.desktop" "$root/usr/share/applications/al76.desktop"
for png in "$here"/icons/*.png; do
  size=$(basename "$png" .png)
  install -D -m 644 "$png" "$root/usr/share/icons/hicolor/${size}x${size}/apps/al76.png"
done
install -m 644 "$here/debian/copyright" "$root/usr/share/doc/al76/copyright"
install -m 755 "$here/debian/postinst" "$here/debian/postrm" "$root/DEBIAN/"
# Files must belong to root and be readable by everyone; only the programs keep the execute bit.
find "$root" -type d -exec chmod 755 {} +
find "$root/opt/al76" -type f -exec chmod 644 {} +
chmod 755 "$root/opt/al76/al76" "$root/opt/al76/createdump" 2>/dev/null || true
find "$root/opt/al76" -name '*.so*' -exec chmod 644 {} +
size=$(du -sk --exclude=DEBIAN "$root" | cut -f1)
sed -e "s/@VERSION@/$version/" -e "s/@ARCH@/$arch/" -e "s/@SIZE@/$size/" "$here/debian/control.in" > "$root/DEBIAN/control"
mkdir -p "$dest"
out="$dest/al76_${version}_${arch}.deb"
dpkg-deb --root-owner-group -Zxz --build "$root" "$out" >/dev/null
echo "$out ($(du -h "$out" | cut -f1))"
