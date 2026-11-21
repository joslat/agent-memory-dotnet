#!/usr/bin/env bash
# The explicit-write tests (native/gate/writetests) against a published library, on plain Ubuntu 24.04 with no .NET,
# with their own throw-away Neo4j (seeded for its schema; these tests write, so they never share the read-only gate's
# store). Usage: native/gate/run-write-gate.sh <bin-dir> <report-dir>
set -euo pipefail
BIN="${1:?bin dir}"; REPORT="$(mkdir -p "${2:?report dir}" && cd "$2" && pwd)"
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
win() { cygpath -m "$1" 2>/dev/null || echo "$1"; }

ENV_FILE="$(win "$REPORT")/neo4j.env"
AM_GATE_NETWORK=am-write-gate-net AM_GATE_PORT=7695 bash "$REPO/native/gate/neo4j-up.sh" "$ENV_FILE"
# shellcheck disable=SC1090
NAME="$(. "$ENV_FILE"; echo "$NAME")"
trap 'docker rm -f "$NAME" >/dev/null 2>&1; rm -f "$ENV_FILE"' EXIT
(set -a; . "$ENV_FILE"; set +a; AM_GATE_PW="$PASSWORD" \
  dotnet run "$REPO/native/gate/seed/seed.cs" -- "bolt://127.0.0.1:$HOST_PORT" neo4j AM_GATE_PW) > "$REPORT/seed.log" 2>&1

set +e
if [ "${WRITE_GATE_EXTRAS:-}" = langgraph ]; then
  # The LangGraph store's tests too: langgraph from the package index, in a fresh virtual environment on the target image.
  RUNTIME_IMAGE=am-speech-gate-pip:24.04 \
  RUNTIME_ENV="-e PYTHONPATH=/native/python -e AGENTMEMORY_NATIVE_LIBRARY=/opt/am/agentmemory_native.so" \
    bash "$REPO/native/gate/run-in-runtime.sh" "$ENV_FILE" "$BIN" sh -c \
    'python3 -m venv /tmp/venv && /tmp/venv/bin/pip install -q langgraph && /tmp/venv/bin/pip show langgraph | sed -n 1,2p \
     && /tmp/venv/bin/python -m unittest discover -v -s /native/gate/writetests -t /native/gate/writetests' \
    > "$REPORT/write-tests.log" 2>&1
else
  RUNTIME_ENV="-e PYTHONPATH=/native/python -e AGENTMEMORY_NATIVE_LIBRARY=/opt/am/agentmemory_native.so" \
    bash "$REPO/native/gate/run-in-runtime.sh" "$ENV_FILE" "$BIN" \
    python3 -m unittest discover -v -s /native/gate/writetests -t /native/gate/writetests > "$REPORT/write-tests.log" 2>&1
fi
TESTS=$?
set -e
SO="$BIN/agentmemory_native.so"
cat > "$REPORT/write-summary.txt" <<EOF
library        $(sha256sum "$SO" | cut -d" " -f1)  $(stat -c %s "$SO") bytes
tests exit     $TESTS
$(grep -E "^(Ran |OK|FAILED)" "$REPORT/write-tests.log" || true)
EOF
cat "$REPORT/write-summary.txt"
exit "$TESTS"
