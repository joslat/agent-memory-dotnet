#!/usr/bin/env bash
# The R1 write path as a Native AOT executable (native/gate/writeprobe: the native library's own explicit-write
# composition and HTTP clients, a stub model inside the probe) against its own throw-away Neo4j, run on plain Ubuntu
# 24.04 with no .NET. Usage: native/gate/run-write-probe.sh <report-dir>
set -euo pipefail
REPORT="$(mkdir -p "${1:?report dir}" && cd "$1" && pwd)"
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
win() { cygpath -m "$1" 2>/dev/null || echo "$1"; }
export MSYS_NO_PATHCONV=1

echo "== publish"
rm -rf "$REPORT/bin"
docker run --rm -v "$(win "$REPO"):/src:ro" -v "$(win "$REPORT"):/out" -v am-speech-nuget:/root/.nuget/packages \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 mcr.microsoft.com/dotnet/sdk:10.0-noble-aot sh -c '
    set -eu
    mkdir -p /work && cd /src
    tar --exclude=bin --exclude=obj -cf - global.json Directory.Build.props README.md resources/logoIcon.png shared src \
      tools/AgentMemory.Native native/gate/writeprobe \
      | tar -C /work -xf -
    cd /work/native/gate/writeprobe
    if dotnet publish WriteProbe.csproj -c Release -r linux-x64 -o /out/bin > /out/publish.log 2>&1
    then echo "publish ok, $(grep "warning IL" /out/publish.log | sort -u | wc -l) unique IL warning lines"
    else echo "publish FAILED"; tail -30 /out/publish.log; exit 1
    fi'

echo "== a throw-away Neo4j of its own"
ENV_FILE="$(win "$REPORT")/neo4j.env"  # Docker on Windows reads Windows paths
AM_GATE_NETWORK=am-write-probe-net AM_GATE_PORT=7698 bash "$REPO/native/gate/neo4j-up.sh" "$ENV_FILE"
# shellcheck disable=SC1090
NAME="$(. "$ENV_FILE"; echo "$NAME")"
trap 'docker rm -f "$NAME" >/dev/null 2>&1; rm -f "$ENV_FILE"' EXIT

echo "== the probe on plain Ubuntu"
set +e
bash "$REPO/native/gate/run-in-runtime.sh" "$ENV_FILE" "$REPORT/bin" \
  /opt/am/WriteProbe bolt://neo4j:7687 neo4j AMP_PASSWORD > "$REPORT/write-probe.json" 2> "$REPORT/write-probe.err"
status=$?
set -e
cat "$REPORT/write-probe.json"
[ -s "$REPORT/write-probe.err" ] && tail -20 "$REPORT/write-probe.err"
echo "probe exit $status"
exit "$status"
