#!/usr/bin/env bash
# Utility with a real model: does a spoken question get the stored fact in its answer, and at what cost in time?
# Three arms on the same recipe, model and fixtures: memory off; memory off with the same facts written into the
# instructions the way the Reachy Mini app's local memory does; AgentMemory recall. Fictional facts, seeded through the
# library's write path with the same embedding model the server uses. Local models only (Ollama on this machine).
# Usage: native/speech/utility/utility-run.sh <report-dir> <server-image> <schema-image> [sessions-per-arm]
#   LLM_MODEL (default llama3.2:3b), EMBED_MODEL (default bge-m3), EMBED_DIMENSIONS (default 1024),
#   MODEL_HOST (default http://host.docker.internal:11434, as seen from the containers; the seed uses 127.0.0.1)
set -uo pipefail
REPORT="$(mkdir -p "${1:?report dir}" && cd "$1" && pwd)"
SERVER_IMAGE="${2:?server image}"; SCHEMA_IMAGE="${3:?schema image}"; SESSIONS="${4:-3}"
LLM_MODEL="${LLM_MODEL:-llama3.2:3b}"; EMBED_MODEL="${EMBED_MODEL:-bge-m3}"; EMBED_DIMENSIONS="${EMBED_DIMENSIONS:-1024}"
MODEL_HOST="${MODEL_HOST:-http://host.docker.internal:11434}"
HERE="$(cd "$(dirname "$0")" && pwd)"
SPEECH="$(cd "$HERE/.." && pwd)"
RECIPE="$SPEECH/recipe"
win() { cygpath -m "$1" 2>/dev/null || echo "$1"; }
export MSYS_NO_PATHCONV=1
PROJECT=am-utility
VOLUME=am-utility-neo4j
BOLT_PORT=17689
PRIVATE="$REPORT/private"
PASSWORD="$(python -I -c "import secrets, string; print(''.join(secrets.choice(string.ascii_letters + string.digits) for _ in range(24)))")"

settings() { # <file> <memory on|off>
  local extra=""
  [ "$2" = on ] && extra="$(win "$RECIPE/compose.test-memory.yml")"
  ( umask 077; mkdir -p "$PRIVATE"; cat > "$1" <<EOF
SERVER_IMAGE=$SERVER_IMAGE
SCHEMA_IMAGE=$SCHEMA_IMAGE
S2S_LLM_BASE_URL=$MODEL_HOST/v1
S2S_LLM_MODEL=$LLM_MODEL
MEMORY=$2
MEMORY_OWNER=utility-owner-a
NEO4J_PASSWORD=$PASSWORD
EMBEDDING_URL=$MODEL_HOST/v1/embeddings
EMBEDDING_MODEL=$EMBED_MODEL
EMBEDDING_DIMENSIONS=$EMBED_DIMENSIONS
EMBEDDING_TIMEOUT_MS=1000
S2S_PUBLISH=127.0.0.1:18767
NEO4J_DATA_VOLUME=$VOLUME
SPEECH_CACHE_VOLUME=am-s2s-cache
COMPOSE_PROJECT=$PROJECT
STATE_DIR=$PRIVATE/state
TEST_BOLT_PORT=$BOLT_PORT
COMPOSE_EXTRA="$extra"
EOF
  )
}
OFF="$PRIVATE/off.env"; ON="$PRIVATE/on.env"
recipe() { bash "$RECIPE/recipe.sh" "$@"; }
cleanup() {
  recipe "$ON" down >/dev/null 2>&1; recipe "$OFF" down >/dev/null 2>&1
  docker volume rm -f "$VOLUME" >/dev/null 2>&1
  rm -rf "$PRIVATE"
}
trap cleanup EXIT
mkdir -p "$PRIVATE"; settings "$OFF" off; settings "$ON" on
cleanup; mkdir -p "$PRIVATE"; settings "$OFF" off; settings "$ON" on

# The same facts as the seed, in the Reachy Mini app's local-memory format, ahead of the client's own instructions.
cat > "$REPORT/instructions-with-facts.txt" <<'EOF'
Things you remember about the user (use this context naturally,
do not recite the list verbatim):
- Works in Zurich
- Has a dog named Mochi
- Favourite food is ramen
- Plays the cello
- Lives in Winterthur
- Prefers concise answers

You are a friendly robot. Answer in one short sentence.
EOF

TURNS=(wav:/out/fixtures/work.wav wav:/out/fixtures/dog.wav wav:/out/fixtures/food.wav wav:/out/fixtures/instrument.wav
       wav:/out/fixtures/hello.wav)
session() { # <label> [instructions file inside /out]
  local label="$1" instructions="${2:-}" environment=()
  [ -n "$instructions" ] && environment=(-e "REALTIME_INSTRUCTIONS_FILE=$instructions")
  docker run --rm --network "${PROJECT}_default" -v "$(win "$SPEECH"):/speech:ro" -v "$(win "$REPORT"):/out" \
    "${environment[@]}" "$SERVER_IMAGE" python3 /speech/realtime_client.py ws://speech:8765/v1/realtime \
    "/out/$label.trace.jsonl" "/out/$label.summary.json" "${TURNS[@]}" > "$REPORT/$label.client.log" 2>&1
  echo "  $label: $(grep -c status=completed "$REPORT/$label.client.log") of ${#TURNS[@]} turns completed"
}

echo "== fixtures"
mkdir -p "$REPORT/fixtures"
docker run --rm -v am-s2s-cache:/root/.cache -v "$(win "$SPEECH"):/speech:ro" -v "$(win "$REPORT"):/out" "$SERVER_IMAGE" \
  python3 /speech/make_fixtures.py /out/fixtures > "$REPORT/fixtures.log" 2>&1
grep -E "\.wav" "$REPORT/fixtures.log"

echo "== AgentMemory recall"
recipe "$ON" up > "$REPORT/on-up.log" 2>&1 || { tail -20 "$REPORT/on-up.log"; exit 1; }
( export APPDATA="${APPDATA:-C:/Users/$USERNAME/AppData/Roaming}" LOCALAPPDATA="${LOCALAPPDATA:-C:/Users/$USERNAME/AppData/Local}"
  AM_SEED_PW="$PASSWORD" dotnet run "$(win "$HERE/seed_utility.cs")" -- "bolt://127.0.0.1:$BOLT_PORT" neo4j AM_SEED_PW \
    "http://127.0.0.1:11434/v1/embeddings" "$EMBED_MODEL" "$EMBED_DIMENSIONS" ) > "$REPORT/seed.log" 2>&1 \
  || { echo "seeding failed"; tail -8 "$REPORT/seed.log"; exit 1; }
grep -c "vector=$EMBED_DIMENSIONS" "$REPORT/seed.log" | sed 's/^/  seeded items with vectors: /'
for n in $(seq 1 "$SESSIONS"); do session "recall-$n"; done
recipe "$ON" logs speech > "$REPORT/recall-speech.log" 2>&1
recipe "$ON" down > /dev/null 2>&1

echo "== memory off, and facts in the instructions"
recipe "$OFF" up > "$REPORT/off-up.log" 2>&1 || { tail -20 "$REPORT/off-up.log"; exit 1; }
for n in $(seq 1 "$SESSIONS"); do session "off-$n"; session "instructions-$n" /out/instructions-with-facts.txt; done
recipe "$OFF" down > /dev/null 2>&1

python -I -X utf8 "$(win "$HERE/utility_summary.py")" "$(win "$REPORT")" "$LLM_MODEL" "$EMBED_MODEL" | tee "$REPORT/utility-summary.md"
