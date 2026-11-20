#!/bin/zsh
# Builds AL-76 for Windows on the Mac: core tests, then a self-contained app (no .NET needed on the PC).
#   scripts/build.sh            tests + win-arm64 (the VM on the Mac)
#   scripts/build.sh x64        tests + win-x64 (normal PCs)
#   scripts/build.sh all        tests + both
# Output in windows/out/<rid>/AL-76.exe. A dev.flag file next to the exe makes the app write
# ultimo-avvio.txt there, read from the Mac through the VM's shared folder.
set -euo pipefail
cd "${0:A:h}/.."
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

case "${1:-arm64}" in
  arm64) rids=(win-arm64) ;;
  x64)   rids=(win-x64) ;;
  all)   rids=(win-arm64 win-x64) ;;
  *) echo "uso: $0 [arm64|x64|all]"; exit 1 ;;
esac

dotnet test tests/AL76.Core.Tests --nologo -v q
for rid in $rids; do
  dotnet publish src/AL76.App -c Release -r $rid --self-contained -o out/$rid --nologo -v q
  # The speed bench shares the same runtime files: AL76.Bench.exe next to AL-76.exe, WAVs in prove/.
  dotnet publish tools/AL76.Bench -c Release -r $rid --self-contained -o out/$rid --nologo -v q
  # Whisper.net copies whisper.cpp for every system; keep only this one (it loads from runtimes/<rid>).
  for other in out/$rid/runtimes/*(N/); do [[ ${other:t} == $rid ]] || rm -rf $other; done
  rm -f out/$rid/ggml-metal.metal out/$rid/*.lib
  # Kokoro voices: KokoroSharp copies all 54+; keep Italian (i*) and English (a*, b*) and the licence.
  mkdir -p out/$rid/voices
  cp ~/.nuget/packages/kokorosharp/0.8.4/content/voices/{a,b,i}?_*.npy ~/.nuget/packages/kokorosharp/0.8.4/content/voices/LICENSE out/$rid/voices/ 2>/dev/null || true
  find out/$rid/voices -mindepth 1 \( -type d -o \( -name '*.npy' ! -name 'a?_*' ! -name 'b?_*' ! -name 'i?_*' \) \) -prune -exec rm -rf {} +
  touch out/$rid/dev.flag
  echo "✓ out/$rid/AL-76.exe"
done
