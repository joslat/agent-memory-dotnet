#!/usr/bin/env bash
# The speech-to-speech baseline without a robot: the server on CPU (Silero VAD, Parakeet STT, Kokoro TTS) with the
# deterministic language-model stand-in, driven by the scripted client with typed and spoken turns. No memory yet.
# Usage: native/speech/run-baseline.sh <report-dir>      (image: am-s2s-cpu:7dbbdaf, see Dockerfile.s2s-cpu)
set -euo pipefail
REPORT="$(mkdir -p "${1:?report dir}" && cd "$1" && pwd)"
HERE="$(cd "$(dirname "$0")" && pwd)"
IMAGE="${S2S_IMAGE:-am-s2s-cpu:7dbbdaf}"
NETWORK=am-speech-net
CACHE=am-s2s-cache
win() { cygpath -m "$1" 2>/dev/null || echo "$1"; }
export MSYS_NO_PATHCONV=1

docker network inspect "$NETWORK" >/dev/null 2>&1 || docker network create "$NETWORK" >/dev/null
cleanup() { docker rm -f am-llm-stub am-s2s >/dev/null 2>&1 || true; }
trap cleanup EXIT
cleanup

docker run -d --name am-llm-stub --network "$NETWORK" -v "$(win "$HERE"):/speech:ro" -v "$(win "$REPORT"):/out" \
  -e LLM_STUB_LOG=/out/llm-requests.jsonl "$IMAGE" python3 /speech/llm_stub.py 8000 >/dev/null

mkdir -p "$REPORT/fixtures"
docker run --rm -v "$CACHE:/root/.cache" -v "$(win "$HERE"):/speech:ro" -v "$(win "$REPORT"):/out" "$IMAGE" \
  python3 /speech/make_fixtures.py /out/fixtures > "$REPORT/fixtures.log" 2>&1
cat "$REPORT/fixtures.log" | grep -E "\.wav" || { tail -20 "$REPORT/fixtures.log"; exit 1; }

docker run -d --name am-s2s --network "$NETWORK" -v "$CACHE:/root/.cache" -v "$(win "$HERE"):/speech:ro" \
  -e S2S_LLM_MODEL=agentmemory-stub -e S2S_LLM_BASE_URL=http://am-llm-stub:8000/v1 \
  "$IMAGE" speech-to-speech serve -f /speech/cpu_speech.yaml --name cpu_speech >/dev/null

START=$(date +%s)
until docker logs am-s2s 2>&1 | grep -q -i -E "Uvicorn running|Application startup complete|listening on"; do
  if ! docker ps -q -f name=am-s2s | grep -q .; then docker logs --tail 40 am-s2s; exit 1; fi
  if [ $(( $(date +%s) - START )) -gt 900 ]; then echo "server not ready after 900 s"; docker logs --tail 40 am-s2s; exit 1; fi
  sleep 3
done
echo "server ready after $(( $(date +%s) - START )) s"

docker run --rm --network "$NETWORK" -v "$(win "$HERE"):/speech:ro" -v "$(win "$REPORT"):/out" "$IMAGE" \
  python3 /speech/realtime_client.py ws://am-s2s:8765/v1/realtime /out/trace.jsonl /out/summary.json \
  "text:Hello robot, how are you today?" wav:/out/fixtures/hello.wav wav:/out/fixtures/work.wav wav:/out/fixtures/style.wav \
  | tee "$REPORT/client.log"
docker logs am-s2s > "$REPORT/server.log" 2>&1
echo "llm requests: $(wc -l < "$REPORT/llm-requests.jsonl")"
