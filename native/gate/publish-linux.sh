#!/usr/bin/env bash
# Publishes tools/AgentMemory.Native for linux-x64 inside the Ubuntu 24.04 .NET SDK image (glibc 2.39, clang).
# The repository is mounted read-only and copied without bin/obj, so the container never touches the checkout's build
# folders. Usage: native/gate/publish-linux.sh <output-dir> [extra dotnet publish arguments]
set -euo pipefail
# The output's parent may not exist yet (a fresh CI checkout has no artifacts/ folder); without it the path resolved to
# "/<name>" and the publish failed.
mkdir -p "$(dirname "${1:?usage: publish-linux.sh <output-dir>}")"
OUT="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
shift
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
mkdir -p "$OUT"
MSYS_NO_PATHCONV=1 docker run --rm \
  -v "$(cygpath -m "$REPO" 2>/dev/null || echo "$REPO"):/src:ro" \
  -v "$(cygpath -m "$OUT" 2>/dev/null || echo "$OUT"):/out" \
  -v am-speech-nuget:/root/.nuget/packages \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  mcr.microsoft.com/dotnet/sdk:10.0-noble-aot sh -c '
    set -eu
    mkdir -p /work && cd /src
    tar --exclude=bin --exclude=obj -cf - global.json Directory.Build.props README.md resources/logoIcon.png shared src tools/AgentMemory.Native | tar -C /work -xf -
    cd /work
    rm -rf /out/bin
    if dotnet publish tools/AgentMemory.Native/AgentMemory.Native.csproj -c Release -r linux-x64 -o /out/bin "$@" > /out/publish.log 2>&1
    then echo "publish ok, $(grep -c "warning IL" /out/publish.log || true) IL warning lines"; ls -l /out/bin/*.so
    else echo "publish FAILED"; tail -30 /out/publish.log; exit 1
    fi
    # Provenance for the wheel manifest and licence notices (native/python/build_wheel.py --provenance): the restore
    # graph, every restored package description, the SDK version and the .NET runtime licence and notices.
    rm -rf /out/provenance && mkdir -p /out/provenance/nuspecs /out/provenance/dotnet
    cp tools/AgentMemory.Native/obj/project.assets.json /out/provenance/
    cp /usr/share/dotnet/LICENSE.txt /usr/share/dotnet/ThirdPartyNotices.txt /out/provenance/dotnet/
    dotnet --version > /out/provenance/sdk-version.txt
    for spec in /root/.nuget/packages/*/*/*.nuspec; do
      version_dir=$(dirname "$spec"); id_dir=$(dirname "$version_dir")
      key="$(basename "$id_dir").$(basename "$version_dir")"
      cp "$spec" "/out/provenance/nuspecs/$key.nuspec"
      for notice in "$version_dir"/*; do
        case "$(basename "$notice" | tr "[:upper:]" "[:lower:]")" in
          license*|licence*|notice*|third-party-notices*|thirdpartynotices*)
            mkdir -p "/out/provenance/packages/$key"; cp "$notice" "/out/provenance/packages/$key/" ;;
        esac
      done
    done' sh "$@"
