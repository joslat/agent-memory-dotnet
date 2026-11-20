#!/usr/bin/env bash
# G-R1, the controlled-write gate, spoken end to end through the recipe with real local models: remember by voice,
# a durable receipt, recall in a fresh session after a full restart, a change of value, forget by voice, and the
# operator's owner erase. Nothing is preloaded: every memory comes from a spoken request in this run.
# Usage: native/speech/gr1/gr1-run.sh <report-dir> <server-image> <schema-image>
#   LLM_MODEL (default llama3.2:3b, also the writer), EMBED_MODEL (default bge-m3), EMBED_DIMENSIONS (default 1024),
#   MODEL_HOST (default http://host.docker.internal:11434, as seen from the containers)
#   WRITER_URL, WRITER_MODEL, WRITER_API_KEY_ENV (the name of an environment variable holding the writer's key): another
#   model for the writer, for example a hosted one, while the conversation stays on LLM_MODEL
set -uo pipefail
REPORT="$(mkdir -p "${1:?report dir}" && cd "$1" && pwd)"
SERVER_IMAGE="${2:?server image}"; SCHEMA_IMAGE="${3:?schema image}"
LLM_MODEL="${LLM_MODEL:-llama3.2:3b}"; EMBED_MODEL="${EMBED_MODEL:-bge-m3}"; EMBED_DIMENSIONS="${EMBED_DIMENSIONS:-1024}"
MODEL_HOST="${MODEL_HOST:-http://host.docker.internal:11434}"
HERE="$(cd "$(dirname "$0")" && pwd)"
SPEECH="$(cd "$HERE/.." && pwd)"
RECIPE="$SPEECH/recipe"
win() { cygpath -m "$1" 2>/dev/null || echo "$1"; }
export MSYS_NO_PATHCONV=1
PROJECT=am-gr1
PUBLISH=127.0.0.1:18768
PRIVATE="$REPORT/private"
SETTINGS="$PRIVATE/gr1.env"
PASSWORD="$(python -I -c "import secrets, string; print(''.join(secrets.choice(string.ascii_letters + string.digits) for _ in range(24)))")"

recipe() { bash "$RECIPE/recipe.sh" "$SETTINGS" "$@"; }
cleanup() {
  recipe down >/dev/null 2>&1
  docker volume rm -f am-gr1-neo4j am-gr1-journal >/dev/null 2>&1
  rm -rf "$PRIVATE"
}
trap cleanup EXIT
cleanup
( umask 077; mkdir -p "$PRIVATE"; cat > "$SETTINGS" <<EOF
SERVER_IMAGE=$SERVER_IMAGE
SCHEMA_IMAGE=$SCHEMA_IMAGE
S2S_LLM_BASE_URL=$MODEL_HOST/v1
S2S_LLM_MODEL=$LLM_MODEL
MEMORY=on
MEMORY_WRITES=on
MEMORY_OWNER=gr1-owner
NEO4J_PASSWORD=$PASSWORD
EMBEDDING_URL=$MODEL_HOST/v1/embeddings
EMBEDDING_MODEL=$EMBED_MODEL
EMBEDDING_DIMENSIONS=$EMBED_DIMENSIONS
EMBEDDING_TIMEOUT_MS=5000
WRITER_URL=${WRITER_URL:-$MODEL_HOST/v1/chat/completions}
WRITER_MODEL=${WRITER_MODEL:-$LLM_MODEL}
WRITER_API_KEY=${WRITER_API_KEY_ENV:+${!WRITER_API_KEY_ENV}}
WRITER_TIMEOUT_MS=180000
S2S_PUBLISH=$PUBLISH
NEO4J_DATA_VOLUME=am-gr1-neo4j
JOURNAL_VOLUME=am-gr1-journal
SPEECH_CACHE_VOLUME=am-s2s-cache
COMPOSE_PROJECT=$PROJECT
STATE_DIR=$PRIVATE/state
EOF
)

capabilities() { curl -s --max-time 10 "http://$PUBLISH/v1/realtime/capabilities"; }
# Waits until no remember or forget is saving (the receipts have ended); prints the memory status it ended on.
wait_saved() {
  local start; start=$(date +%s)
  while true; do
    local status; status="$(capabilities | python -I -c "import json,sys; m=json.load(sys.stdin)['memory']; print(m['writes']['saving'], m['writes']['needsAttention'])" 2>/dev/null)"
    if [ "${status%% *}" = 0 ]; then echo "  writes settled after $(( $(date +%s) - start )) s (needs attention: ${status##* })"; return 0; fi
    if [ $(( $(date +%s) - start )) -gt 600 ]; then echo "  writes still saving after 600 s: $status"; return 1; fi
    sleep 2
  done
}
session() { # <label> <fixture...>
  local label="$1"; shift
  local turns=(); for name in "$@"; do turns+=("wav:/out/fixtures/$name.wav"); done
  docker run --rm --network "${PROJECT}_default" -v "$(win "$SPEECH"):/speech:ro" -v "$(win "$REPORT"):/out" \
    "$SERVER_IMAGE" python3 /speech/realtime_client.py ws://speech:8765/v1/realtime \
    "/out/$label.trace.jsonl" "/out/$label.summary.json" "${turns[@]}" > "$REPORT/$label.client.log" 2>&1
  echo "  $label: $(grep -c status=completed "$REPORT/$label.client.log") of $# turns completed"
}

echo "== fixtures"
mkdir -p "$REPORT/fixtures"
docker run --rm -v am-s2s-cache:/root/.cache -v "$(win "$SPEECH"):/speech:ro" -v "$(win "$REPORT"):/out" "$SERVER_IMAGE" \
  python3 /speech/make_fixtures.py /out/fixtures > "$REPORT/fixtures.log" 2>&1
grep -cE "\.wav" "$REPORT/fixtures.log" | sed 's/^/  fixtures: /'

echo "== up (memory writes on, nothing stored)"
recipe up > "$REPORT/up-1.log" 2>&1 || { tail -20 "$REPORT/up-1.log"; exit 1; }
capabilities > "$REPORT/capabilities-start.json"

echo "== 1. remember by voice"
session s1-remember-green remember_colour
wait_saved
recipe logs speech > "$REPORT/speech-1.log" 2>&1

echo "== restart everything (store and journal on their volumes)"
recipe down > /dev/null 2>&1
recipe up > "$REPORT/up-2.log" 2>&1 || { tail -20 "$REPORT/up-2.log"; exit 1; }

echo "== 2. a fresh session recalls it"
session s2-ask-green ask_colour
echo "== 3. a change of value by voice"
session s3-remember-blue remember_colour_blue
wait_saved
echo "== 4. a fresh session recalls the new value"
session s4-ask-blue ask_colour
echo "== 5. remember, then forget by voice"
session s5-remember-forget-cat remember_cat forget_last
wait_saved
echo "== 6. a fresh session does not recall the forgotten memory"
session s6-ask-cat ask_cat
capabilities > "$REPORT/capabilities-before-erase.json"

echo "== 7. the operator erases the owner"
ERASE_CONFIRMED=yes recipe erase > "$REPORT/erase.json" 2>&1
cat "$REPORT/erase.json"
echo "== 8. a fresh session after the erase"
session s8-ask-after-erase ask_colour ask_cat
capabilities > "$REPORT/capabilities-end.json"
recipe logs speech > "$REPORT/speech-2.log" 2>&1

echo "== what the store holds of the owner now"
NEO4J="$(recipe compose ps -q neo4j)"
docker exec "$NEO4J" sh -c 'cypher-shell -u neo4j -p "$(cut -d/ -f2- /run/secrets/neo4j_auth)" --format plain "MATCH (n) WHERE n.owner_id = \"gr1-owner\" RETURN count(n) AS owned; MATCH (c:Conversation {user_id: \"gr1-owner\"}) RETURN count(c) AS conversations"' \
  > "$REPORT/store-after-erase.txt" 2>&1
cat "$REPORT/store-after-erase.txt"
echo "== journal (state and size only)"
docker run --rm -v am-gr1-journal:/j:ro "$SERVER_IMAGE" sh -c 'ls -l /j/journal; grep -c . /j/journal/journal.jsonl' \
  > "$REPORT/journal-after.txt" 2>&1
cat "$REPORT/journal-after.txt"
docker run --rm -v am-gr1-journal:/j:ro "$SERVER_IMAGE" sh -c 'grep -ciE "green|blue|tom" /j/journal/journal.jsonl || true' \
  > "$REPORT/journal-words-after-erase.txt" 2>&1

python -I -X utf8 "$(win "$HERE/gr1_summary.py")" "$(win "$REPORT")" "$LLM_MODEL" "$EMBED_MODEL" "${WRITER_MODEL:-$LLM_MODEL}" | tee "$REPORT/gr1-summary.md"
