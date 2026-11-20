#!/usr/bin/env bash
# Regression test for recipe.sh's readiness wait (B28): "up" on a speech server that is already running and has a long
# log must see it listening at once. A stand-in "docker" first on PATH serves a log of about 330 KB whose listening line
# is near its top; reading that log through a pipe into "grep -q" under pipefail failed (grep stops reading, the writer
# gets a broken pipe, the pipeline fails), so "up" waited until its timeout. No Docker is used.
# Usage: native/speech/recipe/test-readiness.sh      (exit 0: passed)
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/bin"
cat > "$WORK/bin/docker" <<'EOF'
#!/usr/bin/env bash
case " $* " in
  *" ps -q speech "*) echo 0123456789ab ;;
  " inspect "*) echo 2026-11-20T12:00:00.000000000Z ;;
  " logs "*)
    echo "INFO:     Uvicorn running on http://0.0.0.0:8765 (Press CTRL+C to quit)"
    for i in $(seq 1 6000); do echo "a line of an already long server log, written after startup $i"; done ;;
  *) : ;;
esac
EOF
chmod +x "$WORK/bin/docker"
cat > "$WORK/test.env" <<EOF
SERVER_IMAGE=readiness-test
S2S_LLM_MODEL=readiness-test
S2S_LLM_BASE_URL=http://127.0.0.1:9/v1
MEMORY=off
COMPOSE_PROJECT=readiness-test
STATE_DIR=$WORK/state
READY_TIMEOUT_S=15
EOF
start=$(date +%s)
out="$(PATH="$WORK/bin:$PATH" timeout 60 bash "$HERE/recipe.sh" "$WORK/test.env" up 2>&1)"; code=$?
elapsed=$(( $(date +%s) - start ))
if [ "$code" = 0 ] && [ "$elapsed" -lt 10 ] && grep -q "speech server listening" <<< "$out"; then
  echo "PASS: up saw the running server listening in ${elapsed} s"
  exit 0
fi
echo "FAIL: up exited $code after ${elapsed} s:"; echo "$out" | tail -3
exit 1
