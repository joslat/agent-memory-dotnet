#!/usr/bin/env bash
# Runs the G-AOT gate: the gate tests in the target runtime image (Ubuntu 24.04, python3, no .NET) against the
# published library and the seeded throw-away Neo4j, with a fingerprint of the whole graph taken before and after.
# A read-only profile must leave the fingerprint unchanged. Usage: run-gate.sh <neo4j-env-file> <bin-dir> <report-dir>
set -euo pipefail
ENV_FILE="${1:?neo4j env file}"; BIN="${2:?bin dir}"; REPORT="${3:?report dir}"
HERE="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$REPORT"
# shellcheck disable=SC1090
. "$ENV_FILE"

fingerprint() {
  # The password stays inside the database container: NEO4J_AUTH is already in its environment.
  MSYS_NO_PATHCONV=1 docker exec "$NAME" sh -c 'cypher-shell -u neo4j -p "${NEO4J_AUTH#neo4j/}" --format plain \
    "MATCH (n) WITH n ORDER BY elementId(n) RETURN elementId(n), labels(n), properties(n);
     MATCH ()-[r]->() WITH r ORDER BY elementId(r) RETURN elementId(r), type(r), properties(r);"' | sha256sum | cut -d" " -f1
}

BEFORE="$(fingerprint)"
set +e
RUNTIME_ENV="-e PYTHONPATH=/native/python -e AGENTMEMORY_NATIVE_LIBRARY=/opt/am/agentmemory_native.so" \
  bash "$HERE/run-in-runtime.sh" "$ENV_FILE" "$BIN" \
  python3 -m unittest discover -v -s /native/gate/tests -t /native/gate/tests > "$REPORT/gate-tests.log" 2>&1
TESTS=$?
set -e
AFTER="$(fingerprint)"

SO="$BIN/agentmemory_native.so"
cat > "$REPORT/gate-summary.txt" <<EOF
library        $(sha256sum "$SO" | cut -d" " -f1)  $(stat -c %s "$SO") bytes
runtime image  am-speech-gate-runtime:24.04 ($(docker image inspect -f '{{.Id}}' am-speech-gate-runtime:24.04))
tests exit     $TESTS
graph before   $BEFORE
graph after    $AFTER
graph          $([ "$BEFORE" = "$AFTER" ] && echo unchanged || echo CHANGED)
$(grep -E "^(Ran |OK|FAILED|METRIC)" "$REPORT/gate-tests.log" || true)
EOF
cat "$REPORT/gate-summary.txt"
[ "$TESTS" -eq 0 ] && [ "$BEFORE" = "$AFTER" ]
