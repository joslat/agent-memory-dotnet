#!/usr/bin/env bash
# Builds the recipe's two images from exact inputs: the speech server at one commit of a checkout that has the memory
# hooks (exported with git archive, so only committed code goes in), one agentmemory-native wheel, and the published
# managed command line for the schema step.
# Usage: build-images.sh <server-checkout> <wheel-dir> <cli-publish-dir> <server-tag> <schema-tag>
#   BASE_IMAGE   the CPU speech server base (default am-s2s-cpu:7dbbdaf, from native/speech/Dockerfile.s2s-cpu)
set -euo pipefail
CHECKOUT="${1:?server checkout}"; WHEEL_DIR="${2:?wheel dir}"; CLI_DIR="${3:?cli publish dir}"
SERVER_TAG="${4:?server image tag}"; SCHEMA_TAG="${5:?schema image tag}"
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../../.." && pwd)"
win() { cygpath -m "$1" 2>/dev/null || echo "$1"; }
export MSYS_NO_PATHCONV=1

[ "$(find "$WHEEL_DIR" -maxdepth 1 -name '*.whl' | wc -l)" = 1 ] || { echo "$WHEEL_DIR must hold exactly one wheel" >&2; exit 2; }
[ -f "$CLI_DIR/AgentMemory.Cli.dll" ] || { echo "$CLI_DIR has no AgentMemory.Cli.dll" >&2; exit 2; }
SERVER_COMMIT="$(git -C "$CHECKOUT" rev-parse HEAD)"
[ -z "$(git -C "$CHECKOUT" status --porcelain -- src)" ] || echo "note: $CHECKOUT has uncommitted changes in src; only $SERVER_COMMIT goes in" >&2

TREE="$(mktemp -d)"
trap 'rm -rf "$TREE"' EXIT
git -C "$CHECKOUT" -c core.autocrlf=false archive --format=tar "$SERVER_COMMIT" | tar -x -C "$TREE"

docker build -q -t "$SERVER_TAG" -f "$(win "$HERE")/Dockerfile.server" \
  --build-arg BASE="${BASE_IMAGE:-am-s2s-cpu:7dbbdaf}" --build-arg SERVER_COMMIT="$SERVER_COMMIT" \
  --build-context server="$(win "$TREE")" --build-context wheel="$(win "$(cd "$WHEEL_DIR" && pwd)")" "$(win "$HERE")"
docker build -q -t "$SCHEMA_TAG" -f "$(win "$HERE")/Dockerfile.schema" \
  --build-arg CLI_COMMIT="$(git -C "$(win "$REPO")" rev-parse HEAD)" \
  --build-context cli="$(win "$(cd "$CLI_DIR" && pwd)")" "$(win "$HERE")"
echo "server image $SERVER_TAG (speech server $SERVER_COMMIT), schema image $SCHEMA_TAG"
