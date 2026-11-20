#!/usr/bin/env bash
# Talk to the speech server with AgentMemory in a browser, on this computer, and watch the memory form.
# Starts the recipe (with Neo4j's browser published on 127.0.0.1, compose.inspect.yml) and the speech server's own
# browser voice UI (the demo/ folder of the speech-to-speech checkout, built here as a local image).
# Usage: native/speech/recipe/try-in-browser.sh <settings-file> <speech-to-speech checkout> [up|down|status]
#   up      build the voice UI image, start the recipe and the UI, print the addresses
#   down    stop the UI and the recipe; the memory (Neo4j volume) stays
#   status  the recipe's containers, the UI container and the server's memory status
# The settings file is the recipe's (see TRY_IT.md); the server must be published on 127.0.0.1:8765 (the default).
set -euo pipefail
SETTINGS="${1:?settings file}"; CHECKOUT="${2:?speech-to-speech checkout}"; ACTION="${3:-up}"
HERE="$(cd "$(dirname "$0")" && pwd)"
win() { cygpath -m "$1" 2>/dev/null || echo "$1"; }
export MSYS_NO_PATHCONV=1
export COMPOSE_EXTRA; COMPOSE_EXTRA="$(win "$HERE/compose.inspect.yml")"
UI=agentmemory-speech-ui
IMAGE=agentmemory-speech-ui:local
recipe() { bash "$HERE/recipe.sh" "$SETTINGS" "$@"; }

case "$ACTION" in
  up)
    grep -q -E '^MEMORY=on\b' "$SETTINGS" || { echo "set MEMORY=on in $SETTINGS" >&2; exit 2; }
    [ -f "$CHECKOUT/demo/Dockerfile" ] || { echo "no demo/Dockerfile in $CHECKOUT" >&2; exit 2; }
    echo "== the voice UI image (demo/ of $(git -C "$CHECKOUT" rev-parse --short HEAD 2>/dev/null || echo "the checkout"))"
    docker build -q -t "$IMAGE" "$(win "$CHECKOUT/demo")" > /dev/null
    echo "== the recipe"
    recipe up
    docker rm -f "$UI" > /dev/null 2>&1 || true
    # The browser opens the WebSocket itself, so the address is the host's localhost, not a name inside Docker.
    docker run -d --name "$UI" -p 127.0.0.1:7860:7860 \
      -e SPEECH_TO_SPEECH_URL=ws://localhost:8765/v1/realtime "$IMAGE" > /dev/null
    echo
    echo "talk:    http://localhost:7860   (allow the microphone, then click the orb)"
    echo "memory:  http://localhost:7474   (Neo4j Browser: bolt://localhost:7687, user neo4j, the password in your settings file)"
    echo "status:  http://127.0.0.1:8765/v1/realtime/capabilities"
    echo "stop:    $0 $SETTINGS $CHECKOUT down"
    ;;
  down)
    docker rm -f "$UI" > /dev/null 2>&1 || true
    recipe down
    ;;
  status)
    recipe status
    docker ps -a --filter "name=^$UI\$" --format '{{.Names}}  {{.Status}}'
    curl -s --max-time 5 http://127.0.0.1:8765/v1/realtime/capabilities | python -c \
      "import json, sys; print('memory:', json.dumps(json.load(sys.stdin).get('memory')))" 2>/dev/null \
      || echo "memory: the server does not answer"
    ;;
  *) echo "up, down or status" >&2; exit 2 ;;
esac
