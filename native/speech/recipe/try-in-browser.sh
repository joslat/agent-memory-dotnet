#!/usr/bin/env bash
# Talk to the speech server with AgentMemory in a browser, on this computer, and watch the memory form.
# Starts the recipe (with Neo4j's browser published on 127.0.0.1, compose.inspect.yml) and the speech server's own
# browser voice UI (the demo/ folder of the speech-to-speech checkout), run on this computer: it needs Node.js (npm) and
# Python 3.10 or newer. (The demo's own Dockerfile does not build outside Hugging Face: its lockfile names their
# internal npm mirror and the Dockerfile leaves out the .npmrc that redirects it.)
# Usage: native/speech/recipe/try-in-browser.sh <settings-file> <speech-to-speech checkout> [up|down|status]
#   up      install the UI once, start the recipe and the UI, print the addresses
#   down    stop the UI and the recipe; the memory (Neo4j volume) stays
#   status  the recipe's containers, the UI and the server's memory status
#   trace   follow the server live, coloured, and save the full log and the conversation (logs/ next to the settings)
#   memory  follow what the server remembers for the owner: each memory as it is written, replaced or forgotten
# UI_SERVER_URL sends the UI somewhere else than the server itself, for example through realtime_tap.py.
# The settings file is the recipe's (see TRY_IT.md); the UI and the status check follow its S2S_PUBLISH (default
# 127.0.0.1:8765), which must be on this computer.
# The UI's Python environment, process id and log live next to the settings file (ui-venv, ui.pid, ui.log).
set -euo pipefail
SETTINGS="${1:?settings file}"; CHECKOUT="${2:?speech-to-speech checkout}"; ACTION="${3:-up}"
HERE="$(cd "$(dirname "$0")" && pwd)"
STATE="$(cd "$(dirname "$SETTINGS")" && pwd)"
win() { cygpath -m "$1" 2>/dev/null || echo "$1"; }
export MSYS_NO_PATHCONV=1
export COMPOSE_EXTRA; COMPOSE_EXTRA="$(win "$HERE/compose.inspect.yml")"
VENV="$STATE/ui-venv"; PIDFILE="$STATE/ui.pid"; LOG="$STATE/ui.log"
PY="$VENV/bin/python"; [ -x "$VENV/Scripts/python.exe" ] && PY="$VENV/Scripts/python.exe"
recipe() { bash "$HERE/recipe.sh" "$SETTINGS" "$@"; }
PUBLISH="$(sed -n 's/^S2S_PUBLISH=//p' "$SETTINGS" | tail -1 | tr -d '
')"; PUBLISH="${PUBLISH:-127.0.0.1:8765}"
PORT="${PUBLISH##*:}"
ui_running() { [ -s "$PIDFILE" ] && kill -0 "$(cat "$PIDFILE")" 2> /dev/null; }
stop_ui() { if ui_running; then kill "$(cat "$PIDFILE")" 2> /dev/null || true; fi; rm -f "$PIDFILE"; }

case "$ACTION" in
  up)
    grep -q -E '^MEMORY=on\b' "$SETTINGS" || { echo "set MEMORY=on in $SETTINGS" >&2; exit 2; }
    [ -f "$CHECKOUT/demo/server.py" ] || { echo "no demo/server.py in $CHECKOUT" >&2; exit 2; }
    echo "== the voice UI (demo/ of $(git -C "$CHECKOUT" rev-parse --short HEAD 2> /dev/null || echo "the checkout"))"
    [ -d "$CHECKOUT/demo/node_modules" ] || npm ci --prefix "$CHECKOUT/demo" --omit=dev > "$STATE/ui-install.log" 2>&1
    if [ ! -x "$PY" ]; then
      python -m venv "$VENV"
      PY="$VENV/bin/python"; [ -x "$VENV/Scripts/python.exe" ] && PY="$VENV/Scripts/python.exe"
      "$PY" -m pip install -q --disable-pip-version-check -r "$CHECKOUT/demo/requirements.txt" >> "$STATE/ui-install.log" 2>&1
    fi
    echo "== the recipe"
    recipe up
    stop_ui
    # The browser opens the WebSocket itself, so the address is this computer's localhost.
    ( cd "$CHECKOUT" && SPEECH_TO_SPEECH_URL="${UI_SERVER_URL:-ws://localhost:$PORT/v1/realtime}" \
        nohup "$PY" -m uvicorn --app-dir demo server:app --host 127.0.0.1 --port 7860 > "$LOG" 2>&1 & echo $! > "$PIDFILE" )
    ui_up() { [ "$(curl -s -o /dev/null -w "%{http_code}" --max-time 15 http://127.0.0.1:7860/)" = 200 ]; }
    for _ in $(seq 1 30); do ui_up && break; sleep 2; done
    ui_up || { echo "the UI did not start; see $LOG" >&2; tail -5 "$LOG" >&2; exit 1; }
    echo
    echo "talk:    http://localhost:7860    (allow the microphone, then click the orb)"
    echo "memory:  http://localhost:17474   (Neo4j Browser: connect to bolt://localhost:17687, user neo4j,"
    echo "                                   the password in your settings file)"
    echo "status:  http://127.0.0.1:$PORT/v1/realtime/capabilities"
    echo "stop:    $0 $SETTINGS $CHECKOUT down"
    ;;
  down)
    stop_ui
    recipe down
    ;;
  status)
    recipe status
    if ui_running; then echo "ui: running (pid $(cat "$PIDFILE")), http://localhost:7860"; else echo "ui: not running"; fi
    curl -s --max-time 5 "http://127.0.0.1:$PORT/v1/realtime/capabilities" | python -c \
      "import json, sys; print('memory:', json.dumps(json.load(sys.stdin).get('memory')))" 2> /dev/null \
      || echo "memory: the server does not answer"
    ;;
  trace)
    # A live view of the server: the conversation (what was heard, what was said), memory events, tool calls, per-turn
    # latencies, warnings and errors, coloured; the full log and the conversation are also saved for this run.
    mkdir -p "$STATE/logs"; stamp="$(date +%Y%m%d-%H%M%S)"
    full="$STATE/logs/server-$stamp.log"; talk="$STATE/logs/conversation-$stamp.log"
    id="$(recipe compose ps -q speech)"; [ -n "$id" ] || { echo "the speech server is not running" >&2; exit 1; }
    echo "full log:      $full"
    echo "conversation:  $talk"
    echo "(Ctrl+C to stop following; the server keeps running)"
    echo
    docker logs -f --since "${TRACE_SINCE:-10m}" "$id" 2>&1 \
      | tee "$full" \
      | tee >(grep --line-buffered -E "^(USER|ASSISTANT):|memory: |latency: |Response done|ERROR|Tools: chars" > "$talk") \
      | grep --line-buffered -v -E "HTTP Request: HEAD|Defaulting repo_id|resolve-cache|request_tracing" \
      | sed -u -E \
          -e 's/^(USER:.*)$/\x1b[1;32m\1\x1b[0m/' \
          -e 's/^(ASSISTANT:.*)$/\x1b[1;36m\1\x1b[0m/' \
          -e 's/^(.* - (ERROR|CRITICAL) - .*|.*Traceback.*|.*Error:.*)$/\x1b[1;31m\1\x1b[0m/' \
          -e 's/^(.* - WARNING - .*)$/\x1b[33m\1\x1b[0m/' \
          -e 's/^(.*memory: .*)$/\x1b[35m\1\x1b[0m/'
    ;;
  memory)
    # Every 2 s: the owner's facts and preferences, printed when one appears, is replaced or is forgotten.
    owner="$(sed -n 's/^MEMORY_OWNER=//p' "$SETTINGS" | tail -1 | tr -d '\r')"
    id="$(recipe compose ps -q neo4j)"; [ -n "$id" ] || { echo "Neo4j is not running" >&2; exit 1; }
    query="MATCH (n) WHERE (n:Fact OR n:Preference) AND n.owner_id = '$owner' RETURN n.id, labels(n)[0], coalesce(n.subject + ' | ' + n.predicate + ' | ' + n.object, n.preference), n.invalidated_at IS NULL, toString(n.valid_from), toString(n.valid_until) ORDER BY n.created_at"
    echo "what the server remembers for '$owner' (Ctrl+C to stop)"; echo
    declare -A seen=()
    while true; do
      rows="$(docker exec "$id" sh -c "cypher-shell -u neo4j -p \"\$(cut -d/ -f2- /run/secrets/neo4j_auth)\" --format plain \"$query\"" 2> /dev/null | tail -n +2)"
      [ -n "$rows" ] || { [ ${#seen[@]} -gt 0 ] && { echo "$(date +%H:%M:%S)  (nothing left: erased)"; seen=(); }; }
      while IFS= read -r row; do
        [ -n "$row" ] || continue
        key="$(cut -d, -f1 <<< "$row")"; live="$(cut -d, -f4 <<< "$row" | tr -d ' ')"; state="$key:$live"
        [ "${seen[$key]:-}" = "$state" ] && continue
        text="$(cut -d, -f2- <<< "$row" | sed -E 's/, (TRUE|FALSE)(,.*)$//; s/^ *"?([A-Za-z]+)"?, */\1: /')"
        dates="$(sed -E 's/.*, (TRUE|FALSE), //; s/NULL//g; s/"//g' <<< "$row")"
        [[ "$dates" =~ [0-9] ]] && dates="valid ${dates/, / to }" || dates=""
        if [ "$live" = TRUE ]; then
          printf '\e[1;32m%s  remembered  %s\e[0m  %s\n' "$(date +%H:%M:%S)" "$text" "$dates"
        else
          printf '\e[33m%s  closed      %s\e[0m  %s\n' "$(date +%H:%M:%S)" "$text" "$dates"
        fi
        seen[$key]="$state"
      done <<< "$rows"
      sleep 2
    done
    ;;
  *) echo "up, down, status, trace or memory" >&2; exit 2 ;;
esac
