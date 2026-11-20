#!/usr/bin/env bash
# SPEECH-BENCH: the development corpus (corpus.py) spoken through the recipe, in three arms:
#   off     memory off
#   oracle  memory off, the cluster's gold memories written into the instructions (the Reachy app's local-memory format)
#   memory  AgentMemory, profile learning, learning unasked on; a fresh memory per cluster (the operator's erase)
# Usage: native/speech/bench/bench-run.sh <report-dir> <server-image> <schema-image>
#   ARMS (default "off oracle memory"), REPEATS (default 1), CLUSTERS (default all; e.g. "first" = one per category,
#   or "c03-1,c11-2"), WRITER (stub: the scripted writer, no paid call; real: WRITER_URL, WRITER_MODEL, WRITER_API_KEY_ENV,
#   MAX_WRITER_CALLS (default 60), through writer_meter.py),
#   LLM_MODEL (default llama3.2:3b), EMBED_MODEL (default bge-m3), EMBED_DIMENSIONS (1024),
#   MODEL_HOST (default http://host.docker.internal:11434)
set -uo pipefail
REPORT="$(mkdir -p "${1:?report dir}" && cd "$1" && pwd)"
SERVER_IMAGE="${2:?server image}"; SCHEMA_IMAGE="${3:?schema image}"
ARMS="${ARMS:-off oracle memory}"; REPEATS="${REPEATS:-1}"; CLUSTERS="${CLUSTERS:-all}"; WRITER="${WRITER:-stub}"
LLM_MODEL="${LLM_MODEL:-llama3.2:3b}"; EMBED_MODEL="${EMBED_MODEL:-bge-m3}"; EMBED_DIMENSIONS="${EMBED_DIMENSIONS:-1024}"
MODEL_HOST="${MODEL_HOST:-http://host.docker.internal:11434}"
HERE="$(cd "$(dirname "$0")" && pwd)"
SPEECH="$(cd "$HERE/.." && pwd)"
RECIPE="$SPEECH/recipe"
win() { cygpath -m "$1" 2>/dev/null || echo "$1"; }
export MSYS_NO_PATHCONV=1
PROJECT=am-bench
PUBLISH=127.0.0.1:18773
STUB_PORT=18802
METER_PORT=18803
PRIVATE="$REPORT/private"
SETTINGS="$PRIVATE/bench.env"
PASSWORD="$(python -I -c "import secrets, string; print(''.join(secrets.choice(string.ascii_letters + string.digits) for _ in range(24)))")"
STUB_PID=""

recipe() { bash "$RECIPE/recipe.sh" "$SETTINGS" "$@"; }
cleanup() {
  recipe down >/dev/null 2>&1
  docker volume rm -f am-bench-neo4j am-bench-journal >/dev/null 2>&1
  [ -n "$STUB_PID" ] && kill "$STUB_PID" 2>/dev/null
  rm -rf "$PRIVATE"
}
trap cleanup EXIT
cleanup
rm -f "$REPORT"/speech-*.log

echo "== corpus and fixtures"
python -I "$(win "$HERE/corpus.py")" "$(win "$REPORT/corpus")"
# Fixture names follow the corpus's order, so spoken files are reused only for the exact same list of utterances.
utterances="$(python -I -c "import hashlib, sys; print(hashlib.sha256(open(sys.argv[1], 'rb').read()).hexdigest())" \
  "$(win "$REPORT/corpus/utterances.json")")"
if [ "$(cat "$REPORT/fixtures/utterances.sha256" 2>/dev/null)" != "$utterances" ]; then
  rm -rf "$REPORT/fixtures"
  docker run --rm -v am-s2s-cache:/root/.cache -v "$(win "$SPEECH"):/speech:ro" -v "$(win "$REPORT"):/out" "$SERVER_IMAGE" \
    python3 /speech/make_fixtures.py /out/fixtures /out/corpus/utterances.json > "$REPORT/fixtures.log" 2>&1
  want="$(python -I -c "import json, sys; print(len(json.load(open(sys.argv[1]))))" "$(win "$REPORT/corpus/utterances.json")")"
  have="$(ls "$REPORT/fixtures" 2>/dev/null | grep -c '\.wav$')"
  [ "$have" = "$want" ] || { echo "fixtures: $have of $want made"; tail -5 "$REPORT/fixtures.log"; exit 1; }
  echo "$utterances" > "$REPORT/fixtures/utterances.sha256"
fi
echo "  $(ls "$REPORT/fixtures" | grep -c '\.wav$') fixtures"
SELECTED="$(python -I - "$(win "$REPORT/corpus/corpus.json")" "$CLUSTERS" <<'EOF'
import json, sys
clusters = json.load(open(sys.argv[1]))["clusters"]
want = sys.argv[2]
if want == "first":
    seen = set(); chosen = []
    for c in clusters:
        if c["category"] not in seen:
            seen.add(c["category"]); chosen.append(c["id"])
elif want == "all":
    chosen = [c["id"] for c in clusters]
else:
    chosen = [i for i in want.split(",") if i]
print(" ".join(chosen))
EOF
)"
SELECTED="$(printf "%s" "$SELECTED" | tr -d "\r")"
echo "  clusters: $SELECTED"

if [ "$WRITER" = stub ]; then
  python -I "$(win "$HERE/writer_stub.py")" "$(win "$REPORT/corpus/writer-script.json")" "$STUB_PORT" > "$REPORT/writer-stub.log" 2>&1 &
  STUB_PID=$!
  WRITER_URL="http://host.docker.internal:$STUB_PORT/v1/chat/completions"; WRITER_MODEL="writer-stub"; WRITER_KEY=""
else
  # The paid writer is reached through a counting proxy that holds the key: the server gets none, and every call and
  # its tokens are measured (writer-calls.json); calls beyond MAX_WRITER_CALLS are refused.
  : "${WRITER_URL:?}" "${WRITER_MODEL:?}" "${WRITER_API_KEY_ENV:?}"
  python -I "$(win "$HERE/writer_meter.py")" "$WRITER_URL" "$WRITER_API_KEY_ENV" "$METER_PORT" "${MAX_WRITER_CALLS:-60}" \
    "$(win "$REPORT/writer-calls.json")" > "$REPORT/writer-meter.log" 2>&1 &
  STUB_PID=$!
  sleep 2
  kill -0 "$STUB_PID" 2>/dev/null || { cat "$REPORT/writer-meter.log"; exit 1; }
  WRITER_URL="http://host.docker.internal:$METER_PORT/v1/chat/completions"; WRITER_KEY=""
fi

settings() { # <arm>
  local memory=off writes=off
  [ "$1" = memory ] && memory=on && writes=on
  ( umask 077; mkdir -p "$PRIVATE"; cat > "$SETTINGS" <<EOF
SERVER_IMAGE=$SERVER_IMAGE
SCHEMA_IMAGE=$SCHEMA_IMAGE
S2S_LLM_BASE_URL=$MODEL_HOST/v1
S2S_LLM_MODEL=$LLM_MODEL
MEMORY=$memory
MEMORY_WRITES=$writes
MEMORY_LEARNING=$writes
MEMORY_OBSERVE=$([ "$writes" = on ] && echo 1 || echo 0)
MEMORY_OWNER=bench-owner
NEO4J_PASSWORD=$PASSWORD
EMBEDDING_URL=$MODEL_HOST/v1/embeddings
EMBEDDING_MODEL=$EMBED_MODEL
EMBEDDING_DIMENSIONS=$EMBED_DIMENSIONS
EMBEDDING_TIMEOUT_MS=5000
WRITER_URL=$WRITER_URL
WRITER_MODEL=$WRITER_MODEL
WRITER_API_KEY=$WRITER_KEY
WRITER_TIMEOUT_MS=180000
S2S_PUBLISH=$PUBLISH
NEO4J_DATA_VOLUME=am-bench-neo4j
JOURNAL_VOLUME=am-bench-journal
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
    local saving; saving="$(capabilities | python -I -c "import json,sys; print(json.load(sys.stdin)['memory']['writes']['saving'])" 2>/dev/null)"
    [ "$saving" = 0 ] && return 0
    [ $(( $(date +%s) - start )) -gt 300 ] && { echo "    writes still saving after 300 s"; return 1; }
    sleep 1
  done
}
cypher() { docker exec "$(recipe compose ps -q neo4j)" sh -c "cypher-shell -u neo4j -p \"\$(cut -d/ -f2- /run/secrets/neo4j_auth)\" --format plain \"$1\"" 2>/dev/null | tail -1; }
session() { # <out-prefix> <instructions file or ""> <fixture...>
  local out="$1" instructions="$2"; shift 2
  local turns=() env=(); for f in "$@"; do turns+=("wav:/fx/$f.wav"); done
  [ -n "$instructions" ] && env=(-e "REALTIME_INSTRUCTIONS_FILE=/out/$instructions")
  docker run --rm --network "${PROJECT}_default" "${env[@]}" -v "$(win "$SPEECH"):/speech:ro" -v "$(win "$REPORT/fixtures"):/fx:ro" \
    -v "$(win "$REPORT"):/out" "$SERVER_IMAGE" python3 /speech/realtime_client.py ws://speech:8765/v1/realtime \
    "/out/$out.trace.jsonl" "/out/$out.summary.json" "${turns[@]}" > "$REPORT/$out.client.log" 2>&1
}
journal_last() { docker exec "$(recipe compose ps -q speech)" sh -c "tail -n 1 /var/lib/agentmemory/journal/journal.jsonl" 2>/dev/null; }

run_cluster() { # <arm> <repeat> <cluster id>
  local arm="$1" rep="$2" id="$3" dir="$arm/r$2/$3"
  mkdir -p "$REPORT/$dir"
  python -I - "$(win "$REPORT/corpus/corpus.json")" "$(win "$REPORT/corpus/gold.json")" "$id" "$(win "$REPORT/$dir")" <<'EOF'
import json, pathlib, sys
cluster = next(c for c in json.load(open(sys.argv[1]))["clusters"] if c["id"] == sys.argv[3])
oracle = json.load(open(sys.argv[2]))["clusters"][sys.argv[3]]["oracle"]
out = pathlib.Path(sys.argv[4])
lines = []
for step in cluster["steps"]:
    if "session" in step:
        lines.append(("kill " if step.get("kill_during_write") else "session ") + " ".join(t["fixture"] for t in step["session"]))
    elif step.get("restart"):
        lines.append("restart")
(out / "steps.txt").write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
text = ("Things you remember about the user (use this context naturally,\ndo not recite the list verbatim):\n"
        + "".join(f"- {m}\n" for m in oracle) + "\n" if oracle else "") + "You are a friendly robot. Answer in one short sentence.\n"
(out / "instructions.txt").write_text(text, encoding="utf-8", newline="\n")
EOF
  if [ "$arm" = memory ]; then
    wait_saved
    ERASE_CONFIRMED=yes recipe erase > "$REPORT/$dir/erase.json" 2>&1
  fi
  local instructions=""; [ "$arm" = oracle ] && instructions="$dir/instructions.txt"
  local k=0
  while read -r kind rest; do
    rest="${rest%$'\r'}"; kind="${kind%$'\r'}"
    case "$kind" in
      restart)
        recipe logs speech >> "$REPORT/speech-$arm.log" 2>&1
        recipe down > /dev/null 2>&1; recipe up > "$REPORT/$dir/up.log" 2>&1 || { tail -5 "$REPORT/$dir/up.log"; return 1; } ;;
      session|kill)
        k=$((k + 1))
        if [ "$kind" = kill ] && [ "$arm" = memory ]; then
          # shellcheck disable=SC2086
          session "$dir/s$k" "$instructions" $rest &
          local client=$! result="the write ended before the kill"
          for _ in $(seq 1 600); do
            if grep -q '"kind":"running"' <<< "$(journal_last)"; then
              docker kill "$(recipe compose ps -q speech)" > /dev/null 2>&1; result="killed while running"; break
            fi
            kill -0 "$client" 2>/dev/null || break
            sleep 0.2
          done
          wait "$client" 2>/dev/null
          echo "$result" > "$REPORT/$dir/kill.txt"
          recipe up > "$REPORT/$dir/up.log" 2>&1 || { tail -5 "$REPORT/$dir/up.log"; return 1; }
        else
          # shellcheck disable=SC2086
          session "$dir/s$k" "$instructions" $rest
        fi
        [ "$arm" = memory ] && wait_saved ;;
    esac
  done < "$REPORT/$dir/steps.txt"
  if [ "$arm" = memory ]; then
    cypher "MATCH (n) WHERE (n:Fact OR n:Preference) AND n.owner_id = 'bench-owner' AND n.invalidated_at IS NULL RETURN count(n)" > "$REPORT/$dir/memories.txt"
    cypher "MATCH (n) WHERE (n:Fact OR n:Preference) AND n.owner_id = 'bench-owner' RETURN count(n)" > "$REPORT/$dir/memories-all.txt"
    python -I - "$(win "$REPORT/corpus/gold.json")" "$id" <<'EOF' > "$REPORT/$dir/key.txt"
import json, sys
print(json.load(open(sys.argv[1]))["clusters"][sys.argv[2]]["store"].get("exactly_once", ""))
EOF
    local key; key="$(tr -d '\r\n' < "$REPORT/$dir/key.txt")"
    [ -n "$key" ] && cypher "MATCH (n) WHERE (n:Fact OR n:Preference) AND n.owner_id = 'bench-owner' AND n.invalidated_at IS NULL AND (toLower(coalesce(n.object, '')) CONTAINS toLower('$key') OR toLower(coalesce(n.preference, '')) CONTAINS toLower('$key')) RETURN count(n)" > "$REPORT/$dir/key-count.txt"
  fi
  return 0
}

for arm in $ARMS; do
  echo "== arm $arm"
  settings "$arm"
  recipe up > "$REPORT/up-$arm.log" 2>&1 || { tail -20 "$REPORT/up-$arm.log"; exit 1; }
  for rep in $(seq 1 "$REPEATS"); do
    for id in $SELECTED; do
      started=$(date +%s)
      run_cluster "$arm" "$rep" "$id" || echo "  $arm r$rep $id: FAILED TO RUN"
      echo "  $arm r$rep $id: $(( $(date +%s) - started )) s"
    done
  done
  recipe logs speech >> "$REPORT/speech-$arm.log" 2>&1
  recipe down > /dev/null 2>&1
  docker volume rm -f am-bench-neo4j am-bench-journal > /dev/null 2>&1
done

python -I -X utf8 "$(win "$HERE/bench_summary.py")" "$(win "$REPORT")" "$LLM_MODEL" "$EMBED_MODEL" "$WRITER_MODEL" | tee "$REPORT/bench-summary.md"
