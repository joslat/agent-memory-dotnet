#!/usr/bin/env bash
# Starts a throw-away Neo4j 5.26 for the speech gate on its own Docker network, with a random hex password that is
# never printed. Writes NAME, NETWORK, HOST_PORT and PASSWORD to the env file given as $1 (keep it outside git).
# AM_GATE_NETWORK and AM_GATE_PORT give a second store its own network and port.
# The developer store agent-memory-neo4j-dev is never touched.
set -euo pipefail
ENV_FILE="${1:?usage: neo4j-up.sh <env-file>}"
NETWORK="${AM_GATE_NETWORK:-am-speech-net}"
NAME="am-speech-neo4j-$(date +%s)"
HOST_PORT="${AM_GATE_PORT:-7697}"
PASSWORD="$(od -An -tx1 -N16 /dev/urandom | tr -d ' \n')"

docker network inspect "$NETWORK" >/dev/null 2>&1 || docker network create "$NETWORK" >/dev/null
umask 077
printf 'NAME=%s\nNETWORK=%s\nHOST_PORT=%s\nPASSWORD=%s\n' "$NAME" "$NETWORK" "$HOST_PORT" "$PASSWORD" > "$ENV_FILE"

# The password reaches the container through a private env file, never a command line (docker events show those).
AUTH_FILE="${ENV_FILE}.auth"
printf 'NEO4J_AUTH=neo4j/%s\n' "$PASSWORD" > "$AUTH_FILE"
trap 'rm -f "$AUTH_FILE"' EXIT
MSYS_NO_PATHCONV=1 docker run -d --name "$NAME" --network "$NETWORK" --network-alias neo4j \
  -p "127.0.0.1:${HOST_PORT}:7687" \
  --env-file "$AUTH_FILE" \
  -e NEO4J_server_memory_heap_max__size=1G -e NEO4J_server_memory_pagecache_size=256M \
  neo4j:5.26 >/dev/null

for _ in $(seq 1 90); do
  if docker logs "$NAME" 2>&1 | grep -q "Started\."; then echo "ready: $NAME on 127.0.0.1:${HOST_PORT} (alias neo4j on $NETWORK)"; exit 0; fi
  sleep 2
done
echo "Neo4j did not start in 180 s"; docker logs --tail 20 "$NAME"; exit 1
