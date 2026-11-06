#!/usr/bin/env bash
# Publishes ampack as one self-contained file per platform (PACK-toolset): a pack author needs no .NET. Writes
# artifacts/ampack/<rid>/ampack[.exe] and artifacts/ampack/SHA256SUMS. Usage: eng/publish-ampack.sh [rid ...]
# Default platforms: win-x64 linux-x64 linux-arm64 osx-arm64 osx-x64. Not NativeAOT (see tools/Ampack/Ampack.csproj).
set -euo pipefail
cd "$(dirname "$0")/.."
RIDS=("$@")
[ ${#RIDS[@]} -eq 0 ] && RIDS=(win-x64 linux-x64 linux-arm64 osx-arm64 osx-x64)
OUT=artifacts/ampack
mkdir -p "$OUT"
for rid in "${RIDS[@]}"; do
  echo "== $rid"
  rm -rf "${OUT:?}/$rid"
  dotnet publish tools/Ampack/Ampack.csproj -c Release -r "$rid" --self-contained true -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "$OUT/$rid" -nologo -v:q -m:2
  rm -f "$OUT/$rid"/*.xml
  # The command is ampack on every platform (the assembly keeps its project name).
  for f in "$OUT/$rid"/Ampack "$OUT/$rid"/Ampack.exe; do [ -f "$f" ] && mv "$f" "$(dirname "$f")/$(basename "$f" | sed 's/^Ampack/ampack/')"; done
  ls -la "$OUT/$rid"
done
( cd "$OUT" && find . -type f -name 'ampack*' ! -name '*.pdb' | sort | xargs sha256sum > SHA256SUMS )
cat "$OUT/SHA256SUMS"
