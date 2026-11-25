#!/bin/zsh
# Runs AL-76's Linux checks inside an OrbStack machine (a real Linux, arm64 or amd64), from the Mac.
#   linux/scripts/prova-orb.sh [macchina] test        core tests (Windows) + Linux tests
#   linux/scripts/prova-orb.sh [macchina] deb FILE    installs a .deb with apt (dependencies included) and starts it under Xvfb
#   linux/scripts/prova-orb.sh [macchina] run         starts the published app (out/linux-<arch>) under Xvfb, screenshot in out/
# The sources are copied into the machine first (its own bin/obj and NuGet cache: the Mac build stays untouched).
set -euo pipefail
cd "${0:A:h}/../.."
machine=${1:-al76-2604}; what=${2:-test}
orb -m $machine true || { echo "macchina $machine assente: orb create ubuntu:resolute $machine"; exit 1; }
copy() {
  orb -m $machine bash -c 'command -v rsync >/dev/null || sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq rsync >/dev/null'
  # The paths are expanded inside the machine (its own home), never on the Mac.
  orb -m $machine bash -c "mkdir -p \$HOME/al76/windows && rsync -a --delete --exclude bin --exclude obj --exclude out --exclude .DS_Store '$PWD/linux' \$HOME/al76/ && rsync -a --delete --exclude bin --exclude obj --exclude .DS_Store '$PWD/windows/src' '$PWD/windows/tests' '$PWD/windows/web' \$HOME/al76/windows/"
}
case $what in
  test)
    copy
    orb -m $machine bash -c 'cd ~/al76/linux && export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 &&
      dotnet test tests/AL76.Core.Tests -v q --nologo 2>&1 | grep -E "Superato|Passed|Failed|Non superato|error" | tail -15 &&
      { [[ -d tests/AL76.Linux.Tests ]] && dotnet test tests/AL76.Linux.Tests -v q --nologo 2>&1 | grep -E "Superato|Passed|Failed|Non superato|error" | tail -15 || true; }' ;;
  deb)
    deb=${3:?file .deb}
    orb -m $machine sudo bash -c "DEBIAN_FRONTEND=noninteractive apt-get install -y -qq '$PWD/$deb' 2>&1 | tail -2; dpkg -s al76 | grep -E '^(Status|Version|Architecture)'"
    orb -m $machine bash -c 'command -v al76 && ls -l /usr/share/applications/al76.desktop'
    ;;
  run)
    arch=$(orb -m $machine uname -m); [[ $arch == aarch64 ]] && rid=linux-arm64 || rid=linux-x64
    bin=${3:-$PWD/linux/out/$rid/al76}
    orb -m $machine bash -c "rm -f /tmp/al76-shot.png; export AL76_SCREENSHOT=/tmp/al76-shot.png AL76_QUIT_AFTER=\${AL76_QUIT_AFTER:-12};
      timeout 60 xvfb-run -a -s '-screen 0 1280x800x24' dbus-run-session -- '$bin' 2>&1 | tail -20; ls -l /tmp/al76-shot.png 2>/dev/null && cp /tmp/al76-shot.png '$PWD/linux/out/xvfb-$machine.png'"
    ;;
  *) echo "uso: $0 [macchina] test|deb FILE|run"; exit 1 ;;
esac
