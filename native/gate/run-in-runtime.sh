#!/usr/bin/env bash
# Runs a command in the gate runtime image (Ubuntu 24.04, python3, no .NET) on the gate's Docker network, with the
# published library mounted at /opt/am and native/ at /native. The Neo4j password is passed as AMP_PASSWORD through a
# private env file, never on a command line. Usage: run-in-runtime.sh <neo4j-env-file> <bin-dir> <command...>
# Extra environment for the command: export RUNTIME_ENV="-e NAME=value -e ..." before calling.
# RUNTIME_IMAGE selects another image (default am-speech-gate-runtime:24.04).
set -euo pipefail
ENV_FILE="${1:?neo4j env file}"; BIN="${2:?bin dir}"; shift 2
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
# The gate images are built here when missing, base first: a fresh machine (a CI runner) has neither, and the pip
# image is built FROM the runtime image. The PyPI workflow's gate failed on exactly that ("pull access denied").
GATE="$(cygpath -m "$REPO/native/gate" 2>/dev/null || echo "$REPO/native/gate")"
docker image inspect am-speech-gate-runtime:24.04 >/dev/null 2>&1 \
  || MSYS_NO_PATHCONV=1 docker build -q -t am-speech-gate-runtime:24.04 -f "$GATE/Dockerfile.runtime" "$GATE" >/dev/null
if [ "${RUNTIME_IMAGE:-}" = am-speech-gate-pip:24.04 ]; then
  docker image inspect am-speech-gate-pip:24.04 >/dev/null 2>&1 \
    || MSYS_NO_PATHCONV=1 docker build -q -t am-speech-gate-pip:24.04 -f "$GATE/Dockerfile.pip" "$GATE" >/dev/null
fi
# shellcheck disable=SC1090
. "$ENV_FILE"
PRIVATE="${ENV_FILE}.run"
( umask 077; printf 'AMP_PASSWORD=%s\n' "$PASSWORD" > "$PRIVATE" )
trap 'rm -f "$PRIVATE"' EXIT
BIN_ABS="$(cd "$BIN" && pwd)"
# shellcheck disable=SC2086
MSYS_NO_PATHCONV=1 docker run --rm --network "$NETWORK" \
  -v "$(cygpath -m "$BIN_ABS" 2>/dev/null || echo "$BIN_ABS"):/opt/am:ro" \
  -v "$(cygpath -m "$REPO/native" 2>/dev/null || echo "$REPO/native"):/native:ro" \
  --env-file "$PRIVATE" -e AMP_URI=bolt://neo4j:7687 -e AMP_USER=neo4j -e PYTHONDONTWRITEBYTECODE=1 ${RUNTIME_ENV:-} \
  "${RUNTIME_IMAGE:-am-speech-gate-runtime:24.04}" "$@"
