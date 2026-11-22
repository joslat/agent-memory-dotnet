# Speech server with AgentMemory: the recipe

One way to run the speech-to-speech server on a CPU machine, with or without AgentMemory memory. The host needs Docker
with Compose v2 only: no .NET, no compiler, no Python. Memory comes in three steps, each switched on in the settings
file and each with its own gate passed:

| Settings | What the server does | Gate |
|---|---|---|
| `MEMORY=on` | Recalls what the store holds about one person, before each turn; never writes | G-R0 |
| `+ MEMORY_WRITES=on` | Also remembers and forgets when the person asks, by voice, with truthful receipts; the operator can erase everything | G-R1 |
| `+ MEMORY_LEARNING=on` | Also learns, unasked, what the person tells (never questions, assent or greetings); every write applied once, even across a crash | G-R2 |

| File | What it is |
|---|---|
| `recipe.sh` | The launcher: `up`, `down`, `status`, `logs`, `compose` |
| `example.env` | The settings file to copy and fill in |
| `compose.yml` | The server alone (memory off) |
| `compose.memory.yml` | Added when `MEMORY=on`: Neo4j, the schema step, the memory secret |
| `compose.memory-writes.yml` | Added when `MEMORY_WRITES=on`: the journal's volume, the erase control's token |
| `RUNBOOK.md` | What to do when something goes wrong, for the operator |
| `Dockerfile.server` | The server image: the CPU server, the memory hooks, the `agentmemory-native` wheel |
| `Dockerfile.schema` | The schema step: the managed AgentMemory command line on the .NET runtime |
| `build-images.sh` | Builds both images from exact inputs |
| `recipe-test.sh`, `compose.test*.yml` | The acceptance test |
| `test-readiness.sh` | A regression test for `up`'s readiness wait, with a stand-in `docker` (no Docker needed) |
| `TRY_IT.md`, `try-in-browser.sh`, `compose.inspect.yml` | Talk to it by voice in your browser and watch the memory form in Neo4j's browser |

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

The speech server with the memory hooks and the voice menu is the branch `agentmemory/native-memory-full` of
[github.com/joslat/speech-to-speech](https://github.com/joslat/speech-to-speech/tree/agentmemory/native-memory-full)
(commit `eacc4cfc` is the one tested here), on top of the upstream commit `7dbbdaf`. The branch
`agentmemory/native-memory` is now the memory change alone, as offered upstream in
[huggingface/speech-to-speech#860](https://github.com/huggingface/speech-to-speech/pull/860) (22 Nov 2026).

```bash
git clone --branch agentmemory/native-memory-full https://github.com/joslat/speech-to-speech
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

With `MEMORY_WRITES=off` (read-only), memories come from elsewhere: a managed AgentMemory host (or an import) writes
them for `MEMORY_OWNER`, embedded with the same model and dimension as `EMBEDDING_MODEL` and `EMBEDDING_DIMENSIONS`.

## Remembering and forgetting by voice (`MEMORY_WRITES=on`)

Set `MEMORY_WRITES=on` with `WRITER_URL` and `WRITER_MODEL` (an OpenAI-compatible chat-completions endpoint; it can be
the same model as the conversation). Then the owner can say:

- "Please remember that ...", "remember that ...", "don't forget ...", "can you remember that ...": the server keeps the
  words exactly as heard (never the robot's words) and the library turns them into facts and preferences. A question
  ("do you remember ...?") is not a request.
- "Forget what I just told you", "please forget that": the library stops using what this conversation last remembered.
  It closes those memories; it does not delete them.

Each request goes through the library's journal (its own volume, `JOURNAL_VOLUME`) and ends with a receipt. The robot
is told only what the receipt supports: that it is saving and not confirmed yet, that it is saved, or that it was not
saved; if the outcome comes later, it hears it on the next turn. While a write is being saved, or if one ends
uncertain, recall pauses and turns are answered without memory. `GET /v1/realtime/capabilities` shows how many writes
are saving and whether one needs attention, never their content.

To delete everything remembered for the owner (memories, conversations, and the remembered words in the journal):

```bash
native/speech/recipe/recipe.sh my.env erase      # asks you to type "erase"
```

The server refuses while a write is still being saved; try again a moment later.

## Learning unasked (`MEMORY_LEARNING=on`)

With `MEMORY_WRITES=on` and `MEMORY_LEARNING=on` the library runs its `learning` profile, and the server also gives the
writer what the person tells in passing ("my sister lives in Lisbon"): only their final spoken words, never a question,
bare assent, a greeting or thanks, and only with three words or more; the writer still decides what, if anything, to
keep. The robot is never told about these writes. "Forget what I just told you" reaches them too. `MEMORY_OBSERVE=0`
pauses learning unasked without changing the profile; what was said while it was paused is never learned later.

In this profile every write is applied once, with a receipt in the store: if the server stops while a write runs, the
write is retried after the restart and the receipt decides whether anything is left to do, so nothing is stored twice
and nothing waits for an operator. Typed text from a client is recalled for but never learned from (a client app can
insert text that is not the person speaking). The erase is reported as done only
when nothing of the owner is left in the store. The control behind it (`POST /v1/memory/erase`) needs a token that
`recipe.sh` creates in `STATE_DIR/secrets` and never prints.

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

For what to do when something goes wrong (a server that does not start, a database outage, a write that needs
attention, erasing, upgrades and backups), see [RUNBOOK.md](RUNBOOK.md).

## Upgrade

Build new images, change the tags in the settings file, `down`, `up`. The Neo4j volume is kept and the schema step
completes the schema. The library cannot be swapped under a running server: a new library means a new image and a
restart. To delete all memory: `down`, then `docker volume rm <NEO4J_DATA_VOLUME>` (and `<JOURNAL_VOLUME>` with memory
writes on).

## Privacy

- The memory configuration holds the database password: keep the settings file and `STATE_DIR` private.
- The server's own log lines about memory hold item ids, counts, states and times, never recalled or remembered text.
- With memory writes on, the journal volume holds the words the owner asked to remember (they are the evidence of each
  write), readable only by the server's user; `erase` removes them.
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

```bash
native/speech/recipe/test-readiness.sh
```

Checks that `up` sees a server that is already running, with a long log, as listening at once (it used to wait until
its timeout: a log piped into `grep -q` fails under `pipefail` when grep stops reading early).
