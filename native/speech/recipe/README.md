# Speech server with AgentMemory: the recipe

One way to run the speech-to-speech server on a CPU machine, with or without AgentMemory memory. The host needs Docker
with Compose v2 only: no .NET, no compiler, no Python. Memory is read-only here (release R0): the server recalls what
the store already holds about one person and never writes.

| File | What it is |
|---|---|
| `recipe.sh` | The launcher: `up`, `down`, `status`, `logs`, `compose` |
| `example.env` | The settings file to copy and fill in |
| `compose.yml` | The server alone (memory off) |
| `compose.memory.yml` | Added when `MEMORY=on`: Neo4j, the schema step, the memory secret |
| `Dockerfile.server` | The server image: the CPU server, the memory hooks, the `agentmemory-native` wheel |
| `Dockerfile.schema` | The schema step: the managed AgentMemory command line on the .NET runtime |
| `build-images.sh` | Builds both images from exact inputs |
| `recipe-test.sh`, `compose.test*.yml` | The acceptance test |

## Build the images

```bash
native/gate/publish-linux.sh artifacts/native/current
python native/python/build_wheel.py artifacts/native/current/bin/agentmemory_native.so artifacts/native/wheel \
  --provenance artifacts/native/current/provenance --commit "$(git rev-parse HEAD)"
dotnet publish tools/AgentMemory.Cli/AgentMemory.Cli.csproj -c Release -o artifacts/native/cli -p:UseAppHost=false
docker build -t am-s2s-cpu:7dbbdaf -f native/speech/Dockerfile.s2s-cpu <speech-to-speech checkout at 7dbbdaf>
native/speech/recipe/build-images.sh <speech-to-speech checkout with the memory hooks> artifacts/native/wheel \
  artifacts/native/cli agentmemory-speech-server:local agentmemory-speech-schema:local
```

The server image takes only committed code from the checkout (`git archive`) and records its commit as the image's
revision label. Its build fails if the wheel has no manifest, if the library does not load on the image or its ABI
differs, or if `dotnet` is present.

## Run

```bash
cp native/speech/recipe/example.env ~/agentmemory-speech.env    # then fill it in; it holds the database password
native/speech/recipe/recipe.sh ~/agentmemory-speech.env up       # start, wait until the server listens
native/speech/recipe/recipe.sh ~/agentmemory-speech.env status
native/speech/recipe/recipe.sh ~/agentmemory-speech.env logs     # the server's log
native/speech/recipe/recipe.sh ~/agentmemory-speech.env down     # stop; the memory (Neo4j volume) stays
```

With `MEMORY=off` only the server runs: no database, and the AgentMemory library is never imported.

With `MEMORY=on`, `up`:

1. writes three secret files next to the settings file (`STATE_DIR`, default `./state/secrets`), readable by you only:
   the Neo4j auth file, the password for the schema step, and the AgentMemory configuration (store, the fixed owner,
   the embedding endpoint). No password is in an environment variable or on a command line;
2. starts Neo4j 5.26 on the volume `NEO4J_DATA_VOLUME` and waits until it answers;
3. runs the schema step: `AgentMemory.Cli bootstrap` creates whatever is missing (constraints, indexes, vector indexes
   of `EMBEDDING_DIMENSIONS`, migrations) and leaves what exists alone, so it runs on every start;
4. starts the server, which opens the library at startup (a refused configuration or library stops it, with the reason
   in the log), runs one warm-up recall, and listens.

Memories come from elsewhere in R0: a managed AgentMemory host (or an import) writes them for `MEMORY_OWNER`, embedded
with the same model and dimension as `EMBEDDING_MODEL` and `EMBEDDING_DIMENSIONS`.

## What a turn does

When a spoken turn's final transcript (or a typed turn) becomes a model request, a recall for its text starts at once.
The model request waits for it at most `MEMORY_DEADLINE_MS` (default 450 ms) and then gets the recalled facts and
preferences, labelled as data and not instructions, in its own copy of the current user message. The stored
conversation never holds them. Measured on this recipe's CPU setup: recall 3-70 ms, and no measurable change in time to
first reply audio (greeting turn, same reply with memory on and off: -2 ms).

If the database is down, slow or restarting, turns are answered without memory at the usual speed (1.2 s to first reply
audio in the test, the database stopped). The server keeps running. After a failed or late recall it warms the store up
again in the background every 2 s, and logs `store answers again` when it does. A freshly restarted Neo4j answers its
first query cold (2.6 s measured here), and that cost is paid by the re-warm, not by the user's next turn.

## Upgrade

Build new images, change the tags in the settings file, `down`, `up`. The Neo4j volume is kept and the schema step
completes the schema. The library cannot be swapped under a running server: a new library means a new image and a
restart. To delete all memory: `down`, then `docker volume rm <NEO4J_DATA_VOLUME>`.

## Privacy

- The memory configuration holds the database password: keep the settings file and `STATE_DIR` private.
- The server's own log lines about memory hold item ids, counts and times, never recalled text.
- The speech server prints every user transcript and assistant reply to its console (upstream behaviour, with or without
  memory), and replies may repeat remembered facts. Container logs therefore hold conversation text: restrict who can
  read them and set a log retention.

## Test

```bash
native/speech/recipe/recipe-test.sh <report-dir> <server-image> <schema-image>
```

It uses the deterministic model stand-in and the gate's seeded facts, a fresh Neo4j volume removed afterwards, and
checks: memory off (no database, replies without memory, no memory activity); memory on from nothing (schema step,
vector indexes, warm-up, answers from memory, graph unchanged by recall); a restart with the memory kept; the database
stopped (turns still answered within 3 s, server running) and back (re-warm, recall again without a restart); a damaged
library refused with the reason and what to do; no recalled evidence in the server log.
