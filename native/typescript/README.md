# agentmemory-native for TypeScript

AgentMemory's native library for Node.js (22 and later) over its C ABI (`native/CONTRACT.md`): owner-scoped recall and
journaled writes against Neo4j, with no .NET on the machine. The same library as the Python package `agentmemory-native`,
through [koffi](https://koffi.dev).

Not published yet: the npm names and account are the owner's decision (NATIVE-TS-PUBLISH). `"private": true` keeps
`npm publish` from running until then.

```ts
import { MemoryClient } from "agentmemory-native";

const memory = new MemoryClient({
  schemaVersion: 1,
  profile: "read_only",
  owner: { mode: "fixed", id: "user-42" },
  store: { uri: "neo4j+s://xxxx.databases.neo4j.io", username: "neo4j", password: process.env.NEO4J_PASSWORD },
  embedding: { dimensions: 1536, model: "text-embedding-3-small", endpoint: { url: "https://api.openai.com/v1/embeddings", apiKey: process.env.OPENAI_API_KEY } },
});
try {
  const context = await memory.getContext({ sessionId: "s1", query: "Where does my sister live?", maxConnectionFacts: 4 });
  for (const fact of context.facts) console.log(fact.subject, fact.predicate, fact.object);
} finally {
  memory.close();
}
```

- **Handles are BigInts**, the full 64 bits the ABI gives (a Number keeps 53).
- **The operations return promises.** Each job is admitted at once; one waiter per context settles them as they
  finish. Each of its turns waits at most 1 ms inside the library (`am_v1_wait` returns at once when a job finishes),
  then yields to the event loop, so a result is taken within about a millisecond and the loop is never held longer. It
  runs only while jobs are in flight: an idle client does not keep the process alive. (koffi's worker-thread calls are
  not used: on Linux a process that had used them failed as it exited.)
- **An `AbortSignal` cancels the job in the library first**, then the promise rejects with an `AbortError`.
- **The low level** (`submit`, `Job.poll`, `cancel`, `release`, `state`, `waitAny`) maps one to one onto the ABI and is
  synchronous; the gate runner drives it.
- **Do not install with `--omit=optional`.** The library comes in an optional platform package, and so do koffi's
  prebuilt binaries: without them npm tries to build koffi from source, which needs CMake and a C toolchain.
- **The library comes from a known place only**: an explicit path (`new MemoryClient(config, { library })`), else
  `AGENTMEMORY_NATIVE_LIBRARY`, else the platform package `agentmemory-native-<os>-<cpu>[-gnu]`, checked against its
  `MANIFEST.json` before it is loaded.

## Adapters

Optional, each behind its own import and peer dependency, so the main import needs neither.

- **LangGraph.js** (`agentmemory-native/langgraph`, needs `@langchain/langgraph-checkpoint`): `AgentMemoryStore`, a
  `BaseStore` with the Python adapter's projection. A put remembers `value.text` (or `value.content`) through the journal
  as the context's one owner; a get returns that remember's receipt, not the words; a search returns the facts and
  preferences recall finds for the query, from every key; a delete forgets. Other words under the same key replace the
  earlier ones; the same words again run nothing. A search without a query returns nothing (recall is by meaning), and
  neither filters nor namespace prefixes narrow it (memories are the owner's, not the key's).
- **Vercel AI SDK** (`agentmemory-native/ai`, needs `ai` and `zod`): `agentMemoryTools(memory, { sessionId })` gives a
  `recall` tool (the facts and preferences for a question, one line each, with ids) and a `remember` tool (the person's
  own words, for a write profile; `remember: false` for a read-only context). The owner is never taken from the model.

Both throw when the memory cannot be read (a timeout, an unreachable store, a refusal), so a failure is never read as
"nothing stored". `test/adapters.test.ts` covers them in tier C, on the write gate's store with the stub model.

## The gate

The binding passes the shared gate (`native/gate/cases`, `native/gate/cases/FORMAT.md`) through `gate/runner.ts`, and
the tests `native/gate/REQUIRED.md` asks of each binding through `test/required.test.ts`:

```bash
npm ci && npm run build
AGENTMEMORY_NATIVE_LIBRARY=/path/to/agentmemory_native.so node dist/gate/runner.js --tier A
AMP_URI=bolt://127.0.0.1:7687 AMP_USER=neo4j AMP_PASSWORD=... node dist/gate/runner.js --tier B
GATE_TIER=B AMP_URI=... node --test "dist/test/*.test.js"
```

The runner starts the gate's stub servers (`native/gate/stubs.py`) with `PYTHON` (or `python3`). In CI the four-platform
workflow runs tier A on each build runner and tiers B and C on the store runners (`store_gate.py` with
`GATE_TYPESCRIPT`).
