#!/usr/bin/env bash
# G-R0 latency matrix on this machine, through the recipe (CPU speech, deterministic model stand-in, Neo4j beside the
# server). Every number is end of speech to the first reply audio received by the scripted client, on its own
# monotonic clock (network receipt, not device playback), plus the server's own end-to-end time and its memory lines.
# Scenarios: cold start, warm hit, warm miss, memory off, Neo4j stopped, a second process recalling at 20 per second,
# and CPU load like extraction (half the cores busy). Container memory and CPU are sampled after each scenario.
# Usage: native/speech/recipe/latency-matrix.sh <report-dir> <server-image> <schema-image> [turns-per-scenario]
set -uo pipefail
REPORT="$(mkdir -p "${1:?report dir}" && cd "$1" && pwd)"
SERVER_IMAGE="${2:?server image}"; SCHEMA_IMAGE="${3:?schema image}"; TURNS="${4:-6}"
HERE="$(cd "$(dirname "$0")" && pwd)"
SPEECH="$(cd "$HERE/.." && pwd)"
REPO="$(cd "$HERE/../../.." && pwd)"
win() { cygpath -m "$1" 2>/dev/null || echo "$1"; }
export MSYS_NO_PATHCONV=1
PROJECT=am-latency
VOLUME=am-latency-neo4j
BOLT_PORT=17688
PRIVATE="$REPORT/private"
PASSWORD="$(python -I -c "import secrets, string; print(''.join(secrets.choice(string.ascii_letters + string.digits) for _ in range(24)))")"

settings() { # <file> <memory on|off>
  local extra="$(win "$HERE/compose.test.yml")"
  [ "$2" = on ] && extra="$extra $(win "$HERE/compose.test-memory.yml")"
  ( umask 077; mkdir -p "$PRIVATE"; cat > "$1" <<EOF
SERVER_IMAGE=$SERVER_IMAGE
SCHEMA_IMAGE=$SCHEMA_IMAGE
S2S_LLM_BASE_URL=http://model:8000/v1
S2S_LLM_MODEL=agentmemory-stub
MEMORY=$2
MEMORY_OWNER=gate-owner-a
NEO4J_PASSWORD=$PASSWORD
EMBEDDING_URL=http://model:8000/v1/embeddings
EMBEDDING_MODEL=gate-unit-axes
EMBEDDING_DIMENSIONS=8
S2S_PUBLISH=127.0.0.1:18766
NEO4J_DATA_VOLUME=$VOLUME
SPEECH_CACHE_VOLUME=am-s2s-cache
COMPOSE_PROJECT=$PROJECT
STATE_DIR=$PRIVATE/state
TEST_BOLT_PORT=$BOLT_PORT
TEST_REPORT_DIR=$(win "$REPORT")
COMPOSE_EXTRA="$extra"
EOF
  )
}
OFF="$PRIVATE/off.env"; ON="$PRIVATE/on.env"
recipe() { bash "$HERE/recipe.sh" "$@"; }
cleanup() {
  docker rm -f am-latency-load am-latency-cpu >/dev/null 2>&1
  recipe "$ON" down >/dev/null 2>&1; recipe "$OFF" down >/dev/null 2>&1
  docker volume rm -f "$VOLUME" >/dev/null 2>&1
  rm -rf "$PRIVATE"
}
trap cleanup EXIT
mkdir -p "$PRIVATE"; settings "$OFF" off; settings "$ON" on
cleanup; mkdir -p "$PRIVATE"; settings "$OFF" off; settings "$ON" on

client() { # <label> <turns...>
  local label="$1"; shift
  docker run --rm --network "${PROJECT}_default" -v "$(win "$SPEECH"):/speech:ro" -v "$(win "$REPORT"):/out" \
    "$SERVER_IMAGE" python3 /speech/realtime_client.py ws://speech:8765/v1/realtime "/out/$label.trace.jsonl" \
    "/out/$label.summary.json" "$@" > "$REPORT/$label.client.log" 2>&1
  echo "  $label: $(grep -c status=completed "$REPORT/$label.client.log") of $# turns completed"
}
repeat() { local n="$1" turn="$2" out=(); for _ in $(seq 1 "$n"); do out+=("$turn"); done; echo "${out[@]}"; }
stats() { # <label>
  docker stats --no-stream --format '{{.Name}} {{.MemUsage}} {{.CPUPerc}}' \
    $(docker ps -q --filter "name=$PROJECT-") > "$REPORT/$1.stats.txt" 2>&1
}
CORES="$(docker run --rm "$SERVER_IMAGE" nproc)"
cpu_load_on() {
  docker run -d --name am-latency-cpu "$SERVER_IMAGE" python3 -c "
import multiprocessing as m
def spin():
    while True: pass
[m.Process(target=spin).start() for _ in range($(( CORES / 2 )))]
" > /dev/null
  sleep 3
}
cpu_load_off() { docker rm -f am-latency-cpu > /dev/null; }
neo4j_healthy() {
  for _ in $(seq 1 60); do
    [ "$(docker inspect -f '{{.State.Health.Status}}' "$PROJECT-neo4j-1" 2>/dev/null)" = healthy ] && return 0; sleep 3
  done; return 1
}

echo "== fixtures"
mkdir -p "$REPORT/fixtures"
docker run --rm -v am-s2s-cache:/root/.cache -v "$(win "$SPEECH"):/speech:ro" -v "$(win "$REPORT"):/out" "$SERVER_IMAGE" \
  python3 /speech/make_fixtures.py /out/fixtures > "$REPORT/fixtures.log" 2>&1
WORK=wav:/out/fixtures/work.wav; HELLO=wav:/out/fixtures/hello.wav

echo "== memory off"
recipe "$OFF" up > "$REPORT/off-up.log" 2>&1 || { cat "$REPORT/off-up.log"; exit 1; }
# shellcheck disable=SC2046
client off-hit $(repeat "$TURNS" "$WORK"); client off-miss $(repeat "$TURNS" "$HELLO"); stats off
cpu_load_on
# shellcheck disable=SC2046
client off-cpu-hit $(repeat "$TURNS" "$WORK"); client off-cpu-miss $(repeat "$TURNS" "$HELLO")
cpu_load_off
recipe "$OFF" down > /dev/null 2>&1

echo "== memory on"
recipe "$ON" up > "$REPORT/on-up.log" 2>&1 || { cat "$REPORT/on-up.log"; exit 1; }
( export APPDATA="${APPDATA:-C:/Users/$USERNAME/AppData/Roaming}" LOCALAPPDATA="${LOCALAPPDATA:-C:/Users/$USERNAME/AppData/Local}"
  AM_GATE_PW="$PASSWORD" dotnet run "$(win "$REPO/native/gate/seed/seed.cs")" -- "bolt://127.0.0.1:$BOLT_PORT" neo4j AM_GATE_PW ) \
  > "$REPORT/seed.log" 2>&1 || { echo "seeding failed"; tail -5 "$REPORT/seed.log"; exit 1; }
for round in 1 2 3; do
  since="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  recipe "$ON" compose restart speech > /dev/null 2>&1
  until recipe "$ON" compose logs --since "$since" speech 2>&1 | grep -q "Uvicorn running"; do sleep 2; done
  client "cold-$round" "$WORK"
done
# shellcheck disable=SC2046
client warm-hit $(repeat "$TURNS" "$WORK"); client warm-miss $(repeat "$TURNS" "$HELLO"); stats warm

echo "== a second process recalling at 20 per second"
docker run -d --name am-latency-load --network "${PROJECT}_default" \
  -v "$(win "$PRIVATE/state/secrets"):/secret:ro" -v "$(win "$SPEECH"):/speech:ro" "$SERVER_IMAGE" \
  python3 /speech/recall_load.py /secret/agentmemory.json 20 > /dev/null
sleep 5
# shellcheck disable=SC2046
client concurrent-hit $(repeat "$TURNS" "$WORK"); stats concurrent
docker logs am-latency-load > "$REPORT/recall-load.log" 2>&1; docker rm -f am-latency-load > /dev/null

echo "== CPU load like extraction"
cpu_load_on
# shellcheck disable=SC2046
client cpu-hit $(repeat "$TURNS" "$WORK"); client cpu-miss $(repeat "$TURNS" "$HELLO"); stats cpu
cpu_load_off
echo "  cores in the Docker VM: $CORES, busy: $(( CORES / 2 ))"

echo "== Neo4j stopped"
docker stop "$PROJECT-neo4j-1" > /dev/null
# shellcheck disable=SC2046
client outage $(repeat "$TURNS" "$WORK"); stats outage
docker start "$PROJECT-neo4j-1" > /dev/null; neo4j_healthy
recipe "$ON" logs speech > "$REPORT/speech.log" 2>&1

python -I -X utf8 "$(win "$HERE/latency_summary.py")" "$(win "$REPORT")" "$CORES" | tee "$REPORT/latency-summary.md"
