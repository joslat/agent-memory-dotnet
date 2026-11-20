#!/usr/bin/env bash
# Runs the G-AOT gate: the gate tests in the target runtime image (Ubuntu 24.04, python3, no .NET) against the
# published library and the seeded throw-away Neo4j, with a fingerprint of the whole graph taken before and after.
# A read-only profile must leave the fingerprint unchanged. Usage: run-gate.sh <neo4j-env-file> <bin-dir> <report-dir>
# With GATE_MODE=wheel, <bin-dir> holds a built wheel instead: it is installed with pip into a fresh virtual
# environment on the target image and the tests run against the installed package, with no path overrides.
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

IMAGE=am-speech-gate-runtime:24.04
[ "${GATE_MODE:-library}" = wheel ] && IMAGE=am-speech-gate-pip:24.04
BEFORE="$(fingerprint)"
set +e
if [ "${GATE_MODE:-library}" = wheel ]; then
  docker image inspect am-speech-gate-pip:24.04 >/dev/null 2>&1 \
    || docker build -q -t am-speech-gate-pip:24.04 -f "$HERE/Dockerfile.pip" "$HERE" >/dev/null
  # GATE_EXTRAS=langchain also installs langchain-core from the package index, so the retriever's tests run too.
  RUNTIME_ENV="-e GATE_EXTRAS=${GATE_EXTRAS:-}" RUNTIME_IMAGE=am-speech-gate-pip:24.04 \
    bash "$HERE/run-in-runtime.sh" "$ENV_FILE" "$BIN" sh -c \
    'python3 -m venv /tmp/venv && /tmp/venv/bin/pip install -q --no-index /opt/am/*.whl \
     && if [ "$GATE_EXTRAS" = langchain ]; then /tmp/venv/bin/pip install -q langchain-core \
          && /tmp/venv/bin/pip show langchain-core | head -2; fi \
     && cd /tmp && /tmp/venv/bin/python -m unittest discover -v -s /native/gate/tests -t /native/gate/tests' \
    > "$REPORT/gate-tests.log" 2>&1
else
  RUNTIME_ENV="-e PYTHONPATH=/native/python -e AGENTMEMORY_NATIVE_LIBRARY=/opt/am/agentmemory_native.so" \
    bash "$HERE/run-in-runtime.sh" "$ENV_FILE" "$BIN" \
    python3 -m unittest discover -v -s /native/gate/tests -t /native/gate/tests > "$REPORT/gate-tests.log" 2>&1
fi
TESTS=$?
set -e
AFTER="$(fingerprint)"

SO="$( (ls "$BIN"/agentmemory_native.so "$BIN"/*.whl 2>/dev/null || true) | head -1)"
cat > "$REPORT/gate-summary.txt" <<EOF
library        $(sha256sum "$SO" | cut -d" " -f1)  $(stat -c %s "$SO") bytes
mode           ${GATE_MODE:-library}
runtime image  ${IMAGE} ($(docker image inspect -f '{{.Id}}' "$IMAGE"))
tests exit     $TESTS
graph before   $BEFORE
graph after    $AFTER
graph          $([ "$BEFORE" = "$AFTER" ] && echo unchanged || echo CHANGED)
$(grep -E "^(Ran |OK|FAILED|METRIC)" "$REPORT/gate-tests.log" || true)
EOF
cat "$REPORT/gate-summary.txt"
[ "$TESTS" -eq 0 ] && [ "$BEFORE" = "$AFTER" ]
