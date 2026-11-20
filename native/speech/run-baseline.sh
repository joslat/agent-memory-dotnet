#!/usr/bin/env bash
# The speech-to-speech server without a robot: on CPU (Silero VAD, Parakeet STT, Kokoro TTS) with the deterministic
# language-model stand-in, driven by the scripted client with typed and spoken turns.
#
# Usage: native/speech/run-baseline.sh <report-dir>
#   S2S_IMAGE    image to run (default am-s2s-cpu:7dbbdaf, see Dockerfile.s2s-cpu)
#   S2S_SOURCE   a speech-to-speech checkout whose src/ replaces the installed package (for changed server code)
#   MEMORY_ENV   the gate's Neo4j env file (native/gate/neo4j-up.sh): turns memory on, read-only, against that store,
#                owner gate-owner-a, embeddings from the stand-in; the graph is fingerprinted before and after
#   MEMORY_BIN   the published native library directory (needed with MEMORY_ENV)
set -euo pipefail
REPORT="$(mkdir -p "${1:?report dir}" && cd "$1" && pwd)"
HERE="$(cd "$(dirname "$0")" && pwd)"
IMAGE="${S2S_IMAGE:-am-s2s-cpu:7dbbdaf}"
NETWORK=am-speech-net
CACHE=am-s2s-cache
win() { cygpath -m "$1" 2>/dev/null || echo "$1"; }
export MSYS_NO_PATHCONV=1

docker network inspect "$NETWORK" >/dev/null 2>&1 || docker network create "$NETWORK" >/dev/null
SECRET_DIR="$REPORT/.secret"
cleanup() { docker rm -f am-llm-stub am-s2s >/dev/null 2>&1 || true; rm -rf "$SECRET_DIR"; }
trap cleanup EXIT
cleanup

SERVER_ARGS=()
SERVE=(speech-to-speech serve -f /speech/cpu_speech.yaml --name cpu_speech)
if [ -n "${S2S_SOURCE:-}" ]; then
  # The server writes a tmp folder inside its own package directory, so the read-only source is copied first.
  SERVER_ARGS+=(-v "$(win "$S2S_SOURCE")/src:/fork/src:ro")
  SERVE=(sh -c 'cp -r /fork/src /opt/s2s-src && exec speech-to-speech serve -f /speech/cpu_speech.yaml --name cpu_speech')
  PYPATH="/opt/s2s-src"
fi
fingerprint() { :; }
if [ -n "${MEMORY_ENV:-}" ]; then
  # shellcheck disable=SC1090
  . "$MEMORY_ENV"
  BIN_ABS="$(cd "${MEMORY_BIN:?MEMORY_BIN is required with MEMORY_ENV}" && pwd)"
  ( umask 077; mkdir -p "$SECRET_DIR"
    printf '{"schemaVersion":1,"profile":"read_only","owner":{"mode":"fixed","id":"gate-owner-a"},"store":{"uri":"bolt://neo4j:7687","username":"neo4j","password":"%s"},"embedding":{"dimensions":8,"model":"gate-unit-axes","endpoint":{"url":"http://am-llm-stub:8000/v1/embeddings","timeoutMs":1000}}}\n' \
      "$PASSWORD" > "$SECRET_DIR/memory.json" )
  SERVER_ARGS+=(-v "$(win "$SECRET_DIR"):/secret:ro" -v "$(win "$BIN_ABS"):/opt/am:ro" -v "$(win "$HERE/.."):/native:ro"
                -e S2S_MEMORY_CONFIG=/secret/memory.json -e AGENTMEMORY_NATIVE_LIBRARY=/opt/am/agentmemory_native.so)
  PYPATH="${PYPATH:+$PYPATH:}/native/python"
  fingerprint() {
    docker exec "$NAME" sh -c 'cypher-shell -u neo4j -p "${NEO4J_AUTH#neo4j/}" --format plain \
      "MATCH (n) WITH n ORDER BY elementId(n) RETURN elementId(n), labels(n), properties(n);
       MATCH ()-[r]->() WITH r ORDER BY elementId(r) RETURN elementId(r), type(r), properties(r);"' | sha256sum | cut -d" " -f1
  }
fi
[ -n "${PYPATH:-}" ] && SERVER_ARGS+=(-e "PYTHONPATH=$PYPATH")

docker run -d --name am-llm-stub --network "$NETWORK" -v "$(win "$HERE"):/speech:ro" -v "$(win "$REPORT"):/out" \
  -e LLM_STUB_LOG=/out/llm-requests.jsonl "$IMAGE" python3 /speech/llm_stub.py 8000 >/dev/null

mkdir -p "$REPORT/fixtures"
docker run --rm -v "$CACHE:/root/.cache" -v "$(win "$HERE"):/speech:ro" -v "$(win "$REPORT"):/out" "$IMAGE" \
  python3 /speech/make_fixtures.py /out/fixtures > "$REPORT/fixtures.log" 2>&1
grep -E "\.wav" "$REPORT/fixtures.log" || { tail -20 "$REPORT/fixtures.log"; exit 1; }

BEFORE="$(fingerprint)"
docker run -d --name am-s2s --network "$NETWORK" -v "$CACHE:/root/.cache" -v "$(win "$HERE"):/speech:ro" \
  -e S2S_LLM_MODEL=agentmemory-stub -e S2S_LLM_BASE_URL=http://am-llm-stub:8000/v1 "${SERVER_ARGS[@]}" \
  "$IMAGE" "${SERVE[@]}" >/dev/null

START=$(date +%s)
until grep -q -i -E "Uvicorn running|Application startup complete|listening on" <<< "$(docker logs am-s2s 2>&1)"; do
  if [ -z "$(docker ps -q -f name=am-s2s)" ]; then docker logs --tail 40 am-s2s; exit 1; fi
  if [ $(( $(date +%s) - START )) -gt 900 ]; then echo "server not ready after 900 s"; docker logs --tail 40 am-s2s; exit 1; fi
  sleep 3
done
echo "server ready after $(( $(date +%s) - START )) s"

docker run --rm --network "$NETWORK" -v "$(win "$HERE"):/speech:ro" -v "$(win "$REPORT"):/out" "$IMAGE" \
  python3 /speech/realtime_client.py ws://am-s2s:8765/v1/realtime /out/trace.jsonl /out/summary.json \
  "text:Where do I work?" wav:/out/fixtures/hello.wav wav:/out/fixtures/work.wav wav:/out/fixtures/style.wav \
  | tee "$REPORT/client.log"
docker logs am-s2s > "$REPORT/server.log" 2>&1
echo "llm requests: $(wc -l < "$REPORT/llm-requests.jsonl")"
if [ -n "${MEMORY_ENV:-}" ]; then
  AFTER="$(fingerprint)"
  echo "memory log lines: $(grep -c 'memory:' "$REPORT/server.log" || true)"
  echo "graph $([ "$BEFORE" = "$AFTER" ] && echo unchanged || echo CHANGED) ($BEFORE)"
  [ "$BEFORE" = "$AFTER" ]
fi
