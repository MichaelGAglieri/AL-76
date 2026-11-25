#!/bin/zsh
# Builds AL-76 for Linux on the Mac: a self-contained app (no .NET needed on the PC) and its .deb.
#   linux/scripts/build.sh            arm64 (the VM and OrbStack on the Mac)
#   linux/scripts/build.sh amd64      normal PCs
#   linux/scripts/build.sh all        both
# Output: linux/out/linux-<arch>/al76 and linux/out/al76_<version>_<arch>.deb. The .deb is made inside the
# OrbStack machine (dpkg-deb), ORB_MACHINE or al76-2604.
set -euo pipefail
cd "${0:A:h}/.."
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
machine=${ORB_MACHINE:-al76-2604}

case "${1:-arm64}" in
  arm64) archs=(arm64) ;;
  amd64|x64) archs=(amd64) ;;
  all)   archs=(arm64 amd64) ;;
  *) echo "uso: $0 [arm64|amd64|all]"; exit 1 ;;
esac

# The shared core and voice are compiled from windows/ with the package versions written here: they must match.
for p in ../windows/src/AL76.Core/AL76.Core.csproj ../windows/src/AL76.Speech/AL76.Speech.csproj; do
  mine=src/Shared/${p:h:t}/${p:t}
  for ref in ${(f)"$(grep -o '<PackageReference Include="[^"]*" Version="[^"]*"' $p)"}; do
    grep -qF "$ref" $mine || { echo "✗ $mine: manca o è diverso da windows: $ref"; exit 1; }
  done
done

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
for arch in $archs; do
  [[ $arch == arm64 ]] && rid=linux-arm64 || rid=linux-x64
  rm -rf out/$rid
  dotnet publish src/AL76.Linux.App -c Release -r $rid --self-contained -o out/$rid --nologo -v q
  # Whisper.net copies whisper.cpp for every system; keep only this one (it loads from runtimes/<rid>).
  for other in out/$rid/runtimes/*(N/); do [[ ${other:t} == $rid ]] || rm -rf $other; done
  rm -f out/$rid/ggml-metal.metal out/$rid/*.pdb
  # Kokoro voices: keep Italian (i*) and English (a*, b*) and the licence, as on Windows.
  mkdir -p out/$rid/voices
  cp ~/.nuget/packages/kokorosharp/0.8.4/content/voices/{a,b,i}?_*.npy ~/.nuget/packages/kokorosharp/0.8.4/content/voices/LICENSE out/$rid/voices/ 2>/dev/null || true
  find out/$rid/voices -mindepth 1 \( -type d -o \( -name '*.npy' ! -name 'a?_*' ! -name 'b?_*' ! -name 'i?_*' \) \) -prune -exec rm -rf {} +
  echo "✓ out/$rid/al76"
  orb -m $machine bash packaging/build-deb.sh out/$rid $arch $version out
done
