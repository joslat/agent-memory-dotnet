#!/usr/bin/env bash
# The one supported way to run the speech server with or without AgentMemory memory. See README.md.
# Usage: recipe.sh <settings-file> up|down|status|logs [service]
#   up      writes the secret files (memory on), starts Neo4j, runs the schema step, starts the speech server and
#           waits until it listens
#   down    stops and removes the containers; the Neo4j volume (the memory) and the model cache stay
#   status  the containers and their state
#   logs    the logs of one service (default speech)
#   compose any docker compose command with this recipe's project and files, e.g. "compose stop neo4j"
#   erase   (MEMORY_WRITES=on) deletes everything of MEMORY_OWNER through the running server; asks for the word "erase"
#           unless ERASE_CONFIRMED=yes
# Extra Compose files (tests): COMPOSE_EXTRA="file1.yml file2.yml".
set -euo pipefail
SETTINGS="${1:?settings file}"; ACTION="${2:?up|down|status|logs}"
HERE="$(cd "$(dirname "$0")" && pwd)"
win() { cygpath -m "$1" 2>/dev/null || echo "$1"; }
export MSYS_NO_PATHCONV=1

set -a
# shellcheck disable=SC1090
. "$SETTINGS"
set +a
: "${SERVER_IMAGE:?}" "${S2S_LLM_MODEL:?}" "${S2S_LLM_BASE_URL:?}"
MEMORY="${MEMORY:-off}"
MEMORY_WRITES="${MEMORY_WRITES:-off}"
STATE_DIR="${STATE_DIR:-$(dirname "$SETTINGS")/state}"
mkdir -p "$STATE_DIR"
export SECRETS_DIR; SECRETS_DIR="$(win "$(cd "$STATE_DIR" && pwd)/secrets")"
export COMPOSE_PROJECT="${COMPOSE_PROJECT:-agentmemory-speech}"

FILES=(-f "$(win "$HERE")/compose.yml")
case "$MEMORY" in
  on)  FILES+=(-f "$(win "$HERE")/compose.memory.yml") ;;
  off) ;;
  *)   echo "MEMORY must be on or off, not '$MEMORY'" >&2; exit 2 ;;
esac
case "$MEMORY_WRITES" in
  on)  [ "$MEMORY" = on ] || { echo "MEMORY_WRITES=on needs MEMORY=on" >&2; exit 2; }
       FILES+=(-f "$(win "$HERE")/compose.memory-writes.yml") ;;
  off) ;;
  *)   echo "MEMORY_WRITES must be on or off, not '$MEMORY_WRITES'" >&2; exit 2 ;;
esac
for extra in ${COMPOSE_EXTRA:-}; do FILES+=(-f "$extra"); done
compose() { docker compose -p "$COMPOSE_PROJECT" "${FILES[@]}" "$@"; }

write_secrets() {
  : "${NEO4J_PASSWORD:?}" "${MEMORY_OWNER:?}" "${EMBEDDING_URL:?}" "${EMBEDDING_MODEL:?}" "${EMBEDDING_DIMENSIONS:?}"
  # Values go into JSON and the Neo4j auth file as they are, so only characters that need no escaping are accepted.
  [[ "$NEO4J_PASSWORD" =~ ^[A-Za-z0-9._~+-]{12,}$ ]] \
    || { echo "NEO4J_PASSWORD: use 12 or more of A-Z a-z 0-9 . _ ~ + -" >&2; exit 2; }
  [[ "$MEMORY_OWNER" =~ ^[A-Za-z0-9._:@-]{1,128}$ ]] \
    || { echo "MEMORY_OWNER: use 1-128 of A-Z a-z 0-9 . _ : @ -" >&2; exit 2; }
  [[ "$EMBEDDING_URL" =~ ^https?://[A-Za-z0-9._:/@%-]+$ ]] || { echo "EMBEDDING_URL is not a plain http(s) URL" >&2; exit 2; }
  [[ "$EMBEDDING_MODEL" =~ ^[A-Za-z0-9._:/@-]{1,200}$ ]] || { echo "EMBEDDING_MODEL has unexpected characters" >&2; exit 2; }
  [[ "$EMBEDDING_DIMENSIONS" =~ ^[1-9][0-9]{0,4}$ ]] || { echo "EMBEDDING_DIMENSIONS must be a number" >&2; exit 2; }
  local dir; dir="$(cd "$STATE_DIR" && pwd)/secrets"
  local profile="read_only" writes=""
  if [ "$MEMORY_WRITES" = on ]; then
    : "${WRITER_URL:?WRITER_URL is required with MEMORY_WRITES=on}" "${WRITER_MODEL:?WRITER_MODEL is required with MEMORY_WRITES=on}"
    [[ "$WRITER_URL" =~ ^https?://[A-Za-z0-9._:/@%-]+$ ]] || { echo "WRITER_URL is not a plain http(s) URL" >&2; exit 2; }
    [[ "$WRITER_MODEL" =~ ^[A-Za-z0-9._:/@-]{1,200}$ ]] || { echo "WRITER_MODEL has unexpected characters" >&2; exit 2; }
    [[ "${WRITER_TIMEOUT_MS:-60000}" =~ ^[1-9][0-9]{0,5}$ ]] || { echo "WRITER_TIMEOUT_MS must be a number" >&2; exit 2; }
    local key=""
    if [ -n "${WRITER_API_KEY:-}" ]; then
      # Length apart: a bash pattern cannot repeat more than 255 times.
      [[ "$WRITER_API_KEY" =~ ^[A-Za-z0-9._~+/=-]+$ ]] && [ "${#WRITER_API_KEY}" -ge 8 ] && [ "${#WRITER_API_KEY}" -le 512 ] \
        || { echo "WRITER_API_KEY: use 8-512 of A-Z a-z 0-9 . _ ~ + / = -" >&2; exit 2; }
      key="$(printf ',"apiKey":"%s"' "$WRITER_API_KEY")"
    fi
    profile="explicit_write"
    # The journal lives on its own volume (compose.memory-writes.yml); the library creates this directory, mode 0700.
    writes="$(printf ',"writer":{"model":"%s","endpoint":{"url":"%s","timeoutMs":%s%s}},"state":{"directory":"/var/lib/agentmemory/journal"}' \
      "$WRITER_MODEL" "$WRITER_URL" "${WRITER_TIMEOUT_MS:-60000}" "$key")"
  fi
  ( umask 077
    mkdir -p "$dir"
    printf 'neo4j/%s' "$NEO4J_PASSWORD" > "$dir/neo4j_auth"
    printf '%s' "$NEO4J_PASSWORD" > "$dir/neo4j_password"
    printf '{"schemaVersion":1,"profile":"%s","owner":{"mode":"fixed","id":"%s"},"store":{"uri":"bolt://neo4j:7687","username":"neo4j","password":"%s"},"embedding":{"dimensions":%s,"model":"%s","endpoint":{"url":"%s","timeoutMs":%s}}%s}\n' \
      "$profile" "$MEMORY_OWNER" "$NEO4J_PASSWORD" "$EMBEDDING_DIMENSIONS" "$EMBEDDING_MODEL" "$EMBEDDING_URL" \
      "${EMBEDDING_TIMEOUT_MS:-1000}" "$writes" > "$dir/agentmemory.json"
    # The operator's erase control: a random token, kept only here and in the server's secret file; reused across starts.
    if [ "$MEMORY_WRITES" = on ] && [ ! -s "$dir/memory_control_token" ]; then
      od -An -tx1 -N24 /dev/urandom | tr -d ' \n' > "$dir/memory_control_token"
    fi )
}

case "$ACTION" in
  up)
    if [ "$MEMORY" = on ]; then : "${SCHEMA_IMAGE:?}"; write_secrets; fi
    compose up -d
    start=$(date +%s)
    until compose logs speech 2>&1 | grep -q -E "Uvicorn running|Application startup complete"; do
      if [ -z "$(compose ps -q speech --status running 2>/dev/null)" ] && [ $(( $(date +%s) - start )) -gt 20 ]; then
        echo "the speech server is not running:" >&2; compose logs --tail 30 speech >&2; exit 1
      fi
      if [ $(( $(date +%s) - start )) -gt "${READY_TIMEOUT_S:-900}" ]; then
        echo "the speech server did not start listening" >&2; compose logs --tail 30 speech >&2; exit 1
      fi
      sleep 3
    done
    echo "speech server listening after $(( $(date +%s) - start )) s (memory $MEMORY)"
    ;;
  down)   compose down ;;
  status) compose ps -a ;;
  logs)   compose logs --no-color "${3:-speech}" ;;
  erase)
    [ "$MEMORY_WRITES" = on ] || { echo "erase needs MEMORY_WRITES=on" >&2; exit 2; }
    if [ "${ERASE_CONFIRMED:-}" != yes ]; then
      read -r -p "Delete everything remembered for $MEMORY_OWNER? This cannot be undone. Type erase: " answer
      [ "$answer" = erase ] || { echo "nothing erased"; exit 1; }
    fi
    dir="$(cd "$STATE_DIR" && pwd)/secrets"
    # The token goes to curl in a header file, never on a command line.
    ( umask 077; printf 'Authorization: Bearer %s\n' "$(cat "$dir/memory_control_token")" > "$dir/erase.header" )
    trap 'rm -f "$dir/erase.header"' EXIT
    curl -sS --max-time 180 -H @"$(win "$dir/erase.header")" -H "Content-Type: application/json" \
      -d '{"confirm": true}' "http://${S2S_PUBLISH:-127.0.0.1:8765}/v1/memory/erase"
    echo ;;
  compose) shift 2; compose "$@" ;;
  *)      echo "unknown action $ACTION" >&2; exit 2 ;;
esac
