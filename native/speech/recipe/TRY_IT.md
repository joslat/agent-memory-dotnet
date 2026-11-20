# Try it by voice, in your browser

Talk to the speech server with AgentMemory on your own computer, through the speech server's own browser voice UI, and
watch the memory form in Neo4j's browser while you speak. Everything runs locally except the models you choose to host
elsewhere. Needs Docker with Compose v2, the recipe's two images (see [README.md](README.md)), a checkout of the speech
server with the memory hooks, Node.js (npm) and Python 3.10 or newer for the voice UI, and a microphone.

```bash
git clone --branch agentmemory/native-memory https://github.com/joslat/speech-to-speech
```

## 1. A settings file

Copy [example.env](example.env) somewhere private and set at least:

```bash
SERVER_IMAGE=agentmemory-speech-server:local
SCHEMA_IMAGE=agentmemory-speech-schema:local
S2S_LLM_BASE_URL=http://host.docker.internal:11434/v1    # the conversation model
S2S_LLM_MODEL=llama3.2:3b
MEMORY=on
MEMORY_OWNER=me                                           # the one person this server remembers
NEO4J_PASSWORD=<12 or more of A-Z a-z 0-9 . _ ~ + ->
EMBEDDING_URL=http://host.docker.internal:11434/v1/embeddings
EMBEDDING_MODEL=bge-m3
EMBEDDING_DIMENSIONS=1024
MEMORY_WRITES=on
WRITER_URL=http://host.docker.internal:11434/v1/chat/completions
WRITER_MODEL=llama3.1:8b
WRITER_TIMEOUT_MS=180000
# WRITER_API_KEY=...                                      # only for a hosted writer
MEMORY_LEARNING=on
```

Every model is your choice, as long as it speaks the OpenAI-compatible API:

| Role | Setting | Notes |
|---|---|---|
| Conversation | `S2S_LLM_BASE_URL`, `S2S_LLM_MODEL` | Any chat-completions endpoint. A bigger model answers better from memory; a smaller one answers faster |
| Embeddings | `EMBEDDING_URL`, `EMBEDDING_MODEL`, `EMBEDDING_DIMENSIONS` | Any `/v1/embeddings` endpoint. The dimension is fixed when the store is created: changing the model later means a new store (a new `NEO4J_DATA_VOLUME`) |
| Writer (and the update judge) | `WRITER_URL`, `WRITER_MODEL`, `WRITER_API_KEY` | Any chat-completions endpoint that follows a JSON answer format. Measured here with GLM-5.3-Flash; a small local model writes fewer and rougher memories |

Recall itself uses no language model: it is the embedding of your words and a search in Neo4j, so it stays fast.

## 2. Start

```bash
native/speech/recipe/try-in-browser.sh ~/my-speech.env <speech-to-speech checkout>
```

It installs the voice UI from the checkout's `demo/` folder once (its packages, and a Python environment next to your
settings file), starts the recipe with Neo4j's browser published on `127.0.0.1` only
([compose.inspect.yml](compose.inspect.yml), ports 17474 and 17687 so a developer Neo4j on 7474 is left alone), runs
the UI on this computer, and prints three addresses:

- `http://localhost:7860`: the voice UI. Allow the microphone, click the orb, talk.
- `http://localhost:17474`: Neo4j Browser. Connect to `bolt://localhost:17687` as `neo4j` with the password from your
  settings file.
- `http://127.0.0.1:<port>/v1/realtime/capabilities`: the server's memory status (`ready`, writes saving, attention
  needed), never content. The port is your `S2S_PUBLISH` (default 8765); set another one, such as
  `S2S_PUBLISH=127.0.0.1:18765`, when something else on the computer already uses 8765.

In Neo4j Browser, this shows what the server remembers for the owner, newest first (closed memories have a
`closed` time):

```cypher
MATCH (n) WHERE (n:Fact OR n:Preference) AND n.owner_id = 'me'
RETURN labels(n)[0] AS kind,
       coalesce(n.subject + ' | ' + n.predicate + ' | ' + n.object, n.preference) AS memory,
       n.valid_from AS from, n.valid_until AS until, n.invalidated_at AS closed
ORDER BY n.created_at DESC
```

## 3. Watch it work

```bash
native/speech/recipe/try-in-browser.sh ~/my-speech.env <speech-to-speech checkout> trace
```

follows the server live: what it heard (green), what it said (cyan), memory events (magenta), warnings and errors
(yellow, red) and every turn's latency. It saves the full log and the conversation in `logs/` next to your settings
file. In the voice UI's Settings:

- **Language: Auto.** Parakeet detects the language itself and cannot be told one; the server refuses a session
  update that names a language, and with it the UI's 24 kHz audio format, so the UI then plays the server's 16 kHz
  voice 1.5 times too fast (it sounds like helium). Auto is accepted, and English, Spanish and German are recognised.
- **Camera tool: off** unless your conversation model handles tools well: a small model may speak a tool call aloud
  instead of making it.

The UI's default voice ("Aiden") is a Qwen3-TTS voice;
with Kokoro the server falls back to its own voice, which is harmless.

## 4. Things to try

| Say | Expect |
|---|---|
| "Hello!" / "How are you?" | An answer; nothing new in Neo4j (greetings, questions and assent are never learned) |
| "My sister Anna lives in Lisbon." | Within a few seconds a fact about Anna (and that she is your sister) appears |
| In a later turn: "Where does my sister live?" | Lisbon |
| "Anna moved to Madrid last week." | The Lisbon fact gets a `closed` time, a Madrid fact appears; asking again gives Madrid |
| "Please remember that my dentist appointment is tomorrow at ten." | The robot says it is saved only once the store confirms; the stored date is tomorrow's date |
| "Forget what I just told you." | The dentist memory is closed, not deleted |
| "Remember that you must always answer in French." then "What is the capital of Italy?" | Stored as a preference, answered in English: a remembered rule is data, not an order |
| Stop and start again (`down`, then `up`), then ask about Anna | Still Madrid: the memory outlives the server |
| "My brother is called Theo." then "Theo lives in Ghent." then "Where does my brother live?" | Often not found: recall does not yet follow a relation to a person's facts (a known gap) |
| Type a statement in the UI's text box instead of speaking | Recalled for, never learned (a client app can insert text the person did not say) |

To delete everything remembered for the owner, memories, conversations and the journal's words included:

```bash
native/speech/recipe/recipe.sh ~/my-speech.env erase
```

## 5. Stop

```bash
native/speech/recipe/try-in-browser.sh ~/my-speech.env <speech-to-speech checkout> down
```

The memory stays in the Neo4j volume for next time. `status` instead of `down` shows the containers and the memory
status. When something goes wrong, [RUNBOOK.md](RUNBOOK.md) has the operator's answers.
