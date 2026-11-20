#!/usr/bin/env bash
# Acceptance test of the recipe (design step P05): memory off and on, clean install, schema step, restart with the
# memory kept, the database missing and back, a damaged library refused, private memory text kept out of the logs.
# Uses the deterministic model stand-in and the gate's seeded facts; a fresh Neo4j volume, removed afterwards.
# Usage: native/speech/recipe/recipe-test.sh <report-dir> <server-image> <schema-image>
#   REACHY_APP, REACHY_TEST_IMAGE  optional: a Reachy Mini conversation app checkout with the server memory mode and an
#   image with its dependencies; the app's own status call then runs against this server too
set -uo pipefail
REPORT="$(mkdir -p "${1:?report dir}" && cd "$1" && pwd)"
SERVER_IMAGE="${2:?server image}"; SCHEMA_IMAGE="${3:?schema image}"
HERE="$(cd "$(dirname "$0")" && pwd)"
SPEECH="$(cd "$HERE/.." && pwd)"
REPO="$(cd "$HERE/../../.." && pwd)"
win() { cygpath -m "$1" 2>/dev/null || echo "$1"; }
export MSYS_NO_PATHCONV=1
PROJECT=am-recipe-test
VOLUME=am-recipe-test-neo4j
BOLT_PORT=17687
PRIVATE="$REPORT/private"
RESULTS="$REPORT/results.txt"
: > "$RESULTS"
FAILED=0

pass() { echo "PASS  $*" | tee -a "$RESULTS"; }
fail() { echo "FAIL  $*" | tee -a "$RESULTS"; FAILED=$((FAILED + 1)); }
check() { local name="$1"; shift; if "$@" >> "$REPORT/checks.log" 2>&1; then pass "$name"; else fail "$name"; fi; }

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
S2S_PUBLISH=127.0.0.1:18765
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
settings "$OFF" off; settings "$ON" on
recipe() { bash "$HERE/recipe.sh" "$@"; }

cleanup() {
  recipe "$ON" down >/dev/null 2>&1; recipe "$OFF" down >/dev/null 2>&1
  docker volume rm -f "$VOLUME" >/dev/null 2>&1
  rm -rf "$PRIVATE"
}
trap cleanup EXIT
cleanup
mkdir -p "$PRIVATE"; settings "$OFF" off; settings "$ON" on

client() { # <label> <turns...>
  local label="$1"; shift
  docker run --rm --network "${PROJECT}_default" -v "$(win "$SPEECH"):/speech:ro" -v "$(win "$REPORT"):/out" \
    "$SERVER_IMAGE" python3 /speech/realtime_client.py ws://speech:8765/v1/realtime "/out/$label.trace.jsonl" \
    "/out/$label.summary.json" "$@" > "$REPORT/$label.client.log" 2>&1
  cat "$REPORT/$label.client.log" >> "$REPORT/checks.log"
}
replies_are() { # <label> <expected replies...>
  python -I -X utf8 -c '
import json, sys
turns = json.load(open(sys.argv[1], encoding="utf-8"))
replies = [t["reply"] for t in turns]
print("replies:", replies, "statuses:", [t["status"] for t in turns])
sys.exit(0 if replies == sys.argv[2:] and all(t["status"] == "completed" for t in turns) else 1)
' "$(win "$REPORT/$1.summary.json")" "${@:2}"
}
cypher() { recipe "$ON" compose exec -T neo4j sh -c 'cypher-shell -u neo4j -p "$(cut -d/ -f2- /run/secrets/neo4j_auth)" --format plain "$0"' "$1"; }
fingerprint() {
  cypher "MATCH (n) WITH n ORDER BY elementId(n) RETURN elementId(n), labels(n), properties(n);
          MATCH ()-[r]->() WITH r ORDER BY elementId(r) RETURN elementId(r), type(r), properties(r);" | sha256sum | cut -d" " -f1
}
speech_log() { recipe "$ON" logs speech; }
neo4j_healthy() {
  for _ in $(seq 1 60); do
    [ "$(docker inspect -f '{{.State.Health.Status}}' "$PROJECT-neo4j-1" 2>/dev/null)" = healthy ] && return 0; sleep 3
  done; return 1
}

off_log_has_no_memory() { ! recipe "$OFF" logs speech | grep -q 'memory:'; }
schema_exited_0() { recipe "$ON" compose ps -a schema --format '{{.State}} {{.ExitCode}}' | tee /dev/stderr | grep -q '^exited 0$'; }
rewarmed() {
  for _ in $(seq 1 40); do recipe "$ON" logs speech | grep 'memory: store answers again' && return 0; sleep 2; done
  return 1
}
memory_status_is() { # <expected state, or "off">: the server's capabilities, as a client reads them
  local body
  body="$(docker run --rm --network "${PROJECT}_default" "$SERVER_IMAGE" python3 -c \
    'import urllib.request; print(urllib.request.urlopen("http://speech:8765/v1/realtime/capabilities", timeout=5).read().decode())')" || return 1
  echo "$body" | python -I -c '
import json, sys
memory = json.loads(sys.stdin.read())["memory"]
print("memory:", memory)
text = json.dumps(memory)
leaks = [word for word in ("gate-owner-a", "bolt:", "neo4j", "password") if word in text]
if sys.argv[1] == "off":
    sys.exit(0 if memory == {"enabled": False} else 1)
sys.exit(0 if memory.get("enabled") is True and memory.get("state") == sys.argv[1] and not leaks else 1)' "$1"
}
reachy_app_sees_ready() {
  docker run --rm --network "${PROJECT}_default" -v "$(win "$REACHY_APP"):/app:ro" -w /app \
    -e PYTHONPATH=/app/src -e REACHY_MINI_MEMORY_BACKEND=server -e HF_REALTIME_CONNECTION_MODE=local \
    -e HF_REALTIME_WS_URL=ws://speech:8765/v1/realtime "$REACHY_TEST_IMAGE" python -c '
import asyncio
from reachy_mini_conversation_app.memory_routes import _server_memory_status
status = asyncio.run(_server_memory_status())
print("reachy app sees:", status)
raise SystemExit(0 if status.get("reachable") and status.get("enabled") and status.get("state") == "ready" else 1)'
}
model_never_saw_owner_b() { # the gate's owner-B values; this server is bound to owner A
  test -s "$REPORT/llm-requests.jsonl" || return 1
  grep -c . "$REPORT/llm-requests.jsonl"
  grep -q 'user works in Zurich' "$REPORT/llm-requests.jsonl" || return 1  # owner A's evidence did reach it
  ! grep -q -E 'Barcelona|Prefers detailed answers' "$REPORT/llm-requests.jsonl"
}
warm_up_logged() { recipe "$ON" logs speech | grep 'memory: warm-up recall'; }
vector_indexes_of_8() {
  local dims; dims="$(cypher "SHOW VECTOR INDEXES YIELD options RETURN options.indexConfig['vector.dimensions'] AS d")"
  echo "$dims"; test "$(echo "$dims" | grep -c -x 8)" -ge 1
}

echo "== fixtures"
mkdir -p "$REPORT/fixtures"
docker run --rm -v am-s2s-cache:/root/.cache -v "$(win "$SPEECH"):/speech:ro" -v "$(win "$REPORT"):/out" "$SERVER_IMAGE" \
  python3 /speech/make_fixtures.py /out/fixtures > "$REPORT/fixtures.log" 2>&1
HELLO=wav:/out/fixtures/hello.wav; WORK=wav:/out/fixtures/work.wav; STYLE=wav:/out/fixtures/style.wav

echo "== memory off"
check "off: the server starts with no database and no memory settings" recipe "$OFF" up
check "off: no Neo4j container exists" test -z "$(docker ps -aq --filter "name=$PROJECT-neo4j")"
client off "$HELLO" "$WORK"
check "off: replies without memory" replies_are off "You said: Hello robot, how are you today?." "You said: Where do I work?."
check "off: the server never mentions memory" off_log_has_no_memory
check "off: capabilities say memory is off" memory_status_is off
recipe "$OFF" down > /dev/null 2>&1

echo "== memory on, clean install"
check "on: Neo4j, the schema step and the server start from nothing" recipe "$ON" up
check "on: the schema step exited 0" schema_exited_0
check "on: the schema step created vector indexes of dimension 8" vector_indexes_of_8
check "on: the warm-up recall ran" warm_up_logged
( export APPDATA="${APPDATA:-C:/Users/$USERNAME/AppData/Roaming}" LOCALAPPDATA="${LOCALAPPDATA:-C:/Users/$USERNAME/AppData/Local}"
  AM_GATE_PW="$PASSWORD" dotnet run "$(win "$REPO/native/gate/seed/seed.cs")" -- "bolt://127.0.0.1:$BOLT_PORT" neo4j AM_GATE_PW ) \
  > "$REPORT/seed.log" 2>&1 && pass "on: the gate's facts seeded (test data)" || fail "on: seeding (see seed.log)"
BEFORE="$(fingerprint)"
client on1 "$WORK" "$STYLE" "$HELLO"
check "on: work and style answered from memory, the greeting without" replies_are on1 \
  "From memory: user works in Zurich." "From memory: Prefers concise answers." "You said: Hello robot, how are you today?."
check "on: recall left the graph unchanged" test "$BEFORE" = "$(fingerprint)"
check "on: capabilities say memory is ready, naming no owner or store" memory_status_is ready
if [ -n "${REACHY_APP:-}" ] && [ -n "${REACHY_TEST_IMAGE:-}" ]; then
  check "on: the Reachy app reads the server's memory as ready" reachy_app_sees_ready
fi

echo "== restart with the memory kept"
recipe "$ON" down > /dev/null 2>&1
check "restart: everything starts again on the same volume" recipe "$ON" up
check "restart: the schema step ran again and exited 0" schema_exited_0
AFTER_RESTART="$(fingerprint)"
[ "$BEFORE" = "$AFTER_RESTART" ] && pass "restart: the schema step changed nothing in the graph" \
  || echo "NOTE  restart: the graph fingerprint changed across the schema step ($BEFORE -> $AFTER_RESTART)" | tee -a "$RESULTS"
client on2 "$WORK"
check "restart: the remembered fact is still recalled" replies_are on2 "From memory: user works in Zurich."

echo "== the database missing, then back"
docker stop "$PROJECT-neo4j-1" > /dev/null
client down1 "$WORK" "$HELLO"
check "no database: turns still answered, without memory" replies_are down1 "You said: Where do I work?." "You said: Hello robot, how are you today?."
check "no database: first reply audio within 3 s of speech end" python -I -c '
import json, sys
turns = json.load(open(sys.argv[1], encoding="utf-8"))
values = [t["client_first_audio_s"] for t in turns]
print("first audio:", values); sys.exit(0 if all(v is not None and v < 3.0 for v in values) else 1)' "$(win "$REPORT/down1.summary.json")"
check "no database: the server is still running" test -n "$(docker ps -q --filter "name=$PROJECT-speech-1" --filter status=running)"
check "no database: capabilities say memory is recovering" memory_status_is recovering
docker start "$PROJECT-neo4j-1" > /dev/null
check "database back: healthy" neo4j_healthy
check "database back: the server re-warmed memory by itself" rewarmed
client up1 "$WORK"
check "database back: recall works again without a restart" replies_are up1 "From memory: user works in Zurich."
check "database back: capabilities say memory is ready again" memory_status_is ready

echo "== a damaged library is refused"
recipe "$ON" compose run --rm --no-deps -T --entrypoint sh speech -c '
python3 - <<EOF
import pathlib, agentmemory_native._library as lib
so = lib._packaged_dir() / lib.library_file_name()
data = bytearray(so.read_bytes()); data[len(data) // 2] ^= 0xFF; so.write_bytes(bytes(data))
EOF
exec speech-to-speech serve -f /recipe/pipeline.yaml --name cpu_speech' > "$REPORT/damaged.log" 2>&1
DAMAGED_EXIT=$?
check "damaged: the server refused to start" test "$DAMAGED_EXIT" -ne 0
check "damaged: the log says why and what to do" bash -c "grep -q 'AgentMemory cannot start' '$REPORT/damaged.log' && grep -q 'reinstall agentmemory-native' '$REPORT/damaged.log'"

echo "== logs"
speech_log > "$REPORT/speech.log" 2>&1
check "isolation: owner B's facts never reached the model" model_never_saw_owner_b
check "logs: no recalled evidence block in the server log" bash -c "! grep -q -F '[memory]' '$REPORT/speech.log' && ! grep -q 'Things remembered about the user' '$REPORT/speech.log'"
grep -h "memory:" "$REPORT/speech.log" | sed -E 's/^[^|]*\| //' > "$REPORT/memory-lines.log"

echo
echo "$(grep -c '^PASS' "$RESULTS") passed, $FAILED failed; report in $REPORT"
exit "$FAILED"
