#!/usr/bin/env bash
# G-R2, automatic learning spoken end to end through the recipe (MEMORY_LEARNING=on): the owner tells things without
# asking to remember them, and later sessions recall them; a change of value, a period with learning paused that is
# never backfilled, the speech server killed while a write runs, and the operator's erase. Nothing is preloaded.
# Usage: native/speech/gr2/gr2-run.sh <report-dir> <server-image> <schema-image>
#   LLM_MODEL (default llama3.2:3b), EMBED_MODEL (default bge-m3), EMBED_DIMENSIONS (default 1024),
#   MODEL_HOST (default http://host.docker.internal:11434), WRITER_URL, WRITER_MODEL, WRITER_API_KEY_ENV (see gr1-run.sh)
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
PROJECT=am-gr2
PUBLISH=127.0.0.1:18769
PRIVATE="$REPORT/private"
SETTINGS="$PRIVATE/gr2.env"
PASSWORD="$(python -I -c "import secrets, string; print(''.join(secrets.choice(string.ascii_letters + string.digits) for _ in range(24)))")"

recipe() { bash "$RECIPE/recipe.sh" "$SETTINGS" "$@"; }
cleanup() {
  recipe down >/dev/null 2>&1
  docker volume rm -f am-gr2-neo4j am-gr2-journal >/dev/null 2>&1
  rm -rf "$PRIVATE"
}
trap cleanup EXIT
cleanup
settings() { # <observe 0|1>
  ( umask 077; mkdir -p "$PRIVATE"; cat > "$SETTINGS" <<EOF
SERVER_IMAGE=$SERVER_IMAGE
SCHEMA_IMAGE=$SCHEMA_IMAGE
S2S_LLM_BASE_URL=$MODEL_HOST/v1
S2S_LLM_MODEL=$LLM_MODEL
MEMORY=on
MEMORY_WRITES=on
MEMORY_LEARNING=on
MEMORY_OBSERVE=$1
MEMORY_OWNER=gr2-owner
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
NEO4J_DATA_VOLUME=am-gr2-neo4j
JOURNAL_VOLUME=am-gr2-journal
SPEECH_CACHE_VOLUME=am-s2s-cache
COMPOSE_PROJECT=$PROJECT
STATE_DIR=$PRIVATE/state
EOF
  )
}
capabilities() { curl -s --max-time 10 "http://$PUBLISH/v1/realtime/capabilities"; }
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
restart() { # <label> <observe 0|1>
  recipe logs speech >> "$REPORT/speech.log" 2>&1
  recipe down > /dev/null 2>&1
  settings "$2"
  recipe up > "$REPORT/up-$1.log" 2>&1 || { tail -20 "$REPORT/up-$1.log"; exit 1; }
}
cypher() { docker exec "$(recipe compose ps -q neo4j)" sh -c "cypher-shell -u neo4j -p \"\$(cut -d/ -f2- /run/secrets/neo4j_auth)\" --format plain \"$1\""; }

echo "== fixtures"
mkdir -p "$REPORT/fixtures"
docker run --rm -v am-s2s-cache:/root/.cache -v "$(win "$SPEECH"):/speech:ro" -v "$(win "$REPORT"):/out" "$SERVER_IMAGE" \
  python3 /speech/make_fixtures.py /out/fixtures > "$REPORT/fixtures.log" 2>&1
grep -cE "\.wav" "$REPORT/fixtures.log" | sed 's/^/  fixtures: /'

echo "== up (learning on, nothing stored)"
settings 1
recipe up > "$REPORT/up-1.log" 2>&1 || { tail -20 "$REPORT/up-1.log"; exit 1; }
capabilities > "$REPORT/capabilities-start.json"

echo "== 1. told, not asked; a question is not learned"
session s1-tell-sister tell_sister ask_weather
wait_saved
echo "== 1b. a date told relatively: stored as the date it means, counted from when it was said"
date -u +%Y-%m-%d > "$REPORT/said-dentist.txt"
session s1b-tell-dentist tell_dentist
wait_saved
cypher "MATCH (f:Fact) WHERE f.owner_id = 'gr2-owner' AND toLower(f.subject + ' ' + f.predicate + ' ' + f.object) CONTAINS 'dentist' RETURN f.subject + ' | ' + f.predicate + ' | ' + f.object, toString(f.occurred_on), toString(f.valid_from), toString(f.valid_until)" \
  > "$REPORT/store-dentist.txt" 2>&1
echo "== restart"
restart 2 1
echo "== 2. a fresh session recalls what was told"
session s2-ask-sister ask_sister
echo "== 3. a change, told"
session s3-tell-moved tell_sister_moved
wait_saved
echo "== 4. a fresh session recalls the new value"
session s4-ask-sister ask_sister

echo "== 5. learning paused: told while off is never learned"
restart 3 0
session s5-tell-brother-paused tell_brother
restart 4 1
session s6-ask-brother ask_brother

echo "== 7. the server killed while a learned write runs"
# The kill must land while the write runs. A watcher inside the server's container polls the journal every 50 ms and
# returns when its last record says a write is running; the server is killed at once. Whether it landed is read from the
# journal afterwards (the restart puts an interrupted write back in the queue with a "retry" record), never assumed.
# The writer model can answer before the kill arrives, so up to three told facts are tried, each its own words.
retries() { docker exec "$(recipe compose ps -q speech)" sh -c 'grep -c "\"kind\":\"retry\"" /var/lib/agentmemory/journal/journal.jsonl || true' 2>/dev/null | tail -1; }
: > "$REPORT/kill.txt"; : > "$REPORT/landed.txt"
attempt=0
for pair in "tell_tea:peppermint" "tell_teacher:okonkwo" "tell_locker:4417"; do
  attempt=$((attempt + 1)); fixture="${pair%%:*}"; key="${pair##*:}"
  before="$(retries)"; before="${before:-0}"
  SPEECH_ID="$(recipe compose ps -q speech)"
  session "s7-crash-$attempt" "$fixture" > "$REPORT/s7-$attempt.session.log" 2>&1 &
  CLIENT=$!
  seen="$(docker exec "$SPEECH_ID" sh -c 'for i in $(seq 1 1200); do tail -n 1 /var/lib/agentmemory/journal/journal.jsonl | grep -q "\"kind\":\"running\"" && { echo running; exit 0; }; sleep 0.05; done; echo timeout' 2>/dev/null)"
  [ "$seen" = running ] && docker kill "$SPEECH_ID" > /dev/null 2>&1
  wait "$CLIENT" 2>/dev/null
  recipe logs speech >> "$REPORT/speech.log" 2>&1
  recipe up > "$REPORT/up-crash-$attempt.log" 2>&1 || { tail -20 "$REPORT/up-crash-$attempt.log"; exit 1; }
  wait_saved
  after="$(retries)"; after="${after:-0}"
  echo "attempt $attempt ($fixture): watcher saw $seen; journal retries $before -> $after" | tee -a "$REPORT/kill.txt" | sed 's/^/  /'
  if [ "$after" -gt "$before" ]; then
    echo "$key $((after - before))" > "$REPORT/landed.txt"
    cypher "MATCH (n) WHERE (n:Fact OR n:Preference) AND n.owner_id = 'gr2-owner' AND n.invalidated_at IS NULL AND toLower(coalesce(n.object, '') + ' ' + coalesce(n.preference, '') + ' ' + coalesce(n.subject, '')) CONTAINS '$key' RETURN count(n)" \
      > "$REPORT/store-once.txt" 2>&1
    break
  fi
done
session s8-ask-tea ask_tea
echo "== 8b. a second taste told: added, the first not replaced"
session s8b-tell-green-tea tell_green_tea
wait_saved
cypher "MATCH (n) WHERE (n:Fact OR n:Preference) AND n.owner_id = 'gr2-owner' AND n.invalidated_at IS NULL AND toLower(coalesce(n.object, '') + ' ' + coalesce(n.preference, '')) CONTAINS 'peppermint' RETURN count(n)" > "$REPORT/store-first-taste-live.txt" 2>&1
cypher "MATCH (n) WHERE (n:Fact OR n:Preference) AND n.owner_id = 'gr2-owner' AND n.invalidated_at IS NULL AND toLower(coalesce(n.object, '') + ' ' + coalesce(n.preference, '')) CONTAINS 'green tea' RETURN count(n)" > "$REPORT/store-green-tea-live.txt" 2>&1
capabilities > "$REPORT/capabilities-before-erase.json"

echo "== 9. the operator erases the owner"
ERASE_CONFIRMED=yes recipe erase > "$REPORT/erase.json" 2>&1
cat "$REPORT/erase.json"
session s9-ask-after-erase ask_sister ask_tea
capabilities > "$REPORT/capabilities-end.json"
recipe logs speech >> "$REPORT/speech.log" 2>&1
cypher "MATCH (n) WHERE n.owner_id = 'gr2-owner' RETURN count(n) AS owned" > "$REPORT/store-after-erase.txt" 2>&1

python -I -X utf8 "$(win "$HERE/gr2_summary.py")" "$(win "$REPORT")" "$LLM_MODEL" "$EMBED_MODEL" "${WRITER_MODEL:-$LLM_MODEL}" | tee "$REPORT/gr2-summary.md"
