/**
 * NATIVE-TS-ADAPTERS: the LangGraph.js store and the Vercel AI SDK tools, on the write gate's store with the stub
 * model (tier C), and a recall failure that is never read as "nothing stored" (no store needed). Python's LangGraph
 * tests are native/gate/writetests/test_writes.py::Test5LangGraph; these mirror them.
 * GATE_TIER=C AMP_URI=... node --test dist/test/adapters.test.js
 */
import assert from "node:assert/strict";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { after, before, describe, test } from "node:test";
import { MemoryClient } from "../src/index.js";
import { AgentMemoryStore, StoreWriteError } from "../src/langgraph.js";
import { agentMemoryTools, MemoryUnavailableError } from "../src/ai.js";
import { Stubs } from "../gate/runner.js";

const TIER = (process.env.GATE_TIER ?? "A").toUpperCase();
const RUN = Math.random().toString(16).slice(2, 10); // a namespace of its own, so the tests can run again on one store

function writeConfig(stubUrl: string): Record<string, unknown> {
  return {
    schemaVersion: 1, profile: "explicit_write", owner: { mode: "fixed", id: "gate-writer-owner" },
    store: { uri: process.env.AMP_URI, username: process.env.AMP_USER ?? "neo4j", password: process.env.AMP_PASSWORD },
    embedding: { dimensions: 8, model: "stub-embedder", endpoint: { url: `${stubUrl}/letters/v1/embeddings`, timeoutMs: 5000 } },
    writer: { model: "stub-writer", endpoint: { url: `${stubUrl}/v1/chat/completions`, timeoutMs: 30_000 } },
    state: { directory: join(mkdtempSync(join(tmpdir(), "am-ts-state-")), "state") },
    limits: { defaultTimeoutMs: 20_000 },
  };
}

// What the AI SDK passes an execute function, enough for these tools.
const toolOptions: any = { toolCallId: "call-1", messages: [] };

describe("the adapters on the write store", { skip: TIER !== "C" && "tier C: needs the write gate's store" }, () => {
  let stubs: Stubs;
  let memory: MemoryClient;
  let store: AgentMemoryStore;
  const ns = (...parts: string[]) => ["user", RUN, ...parts];

  before(async () => {
    stubs = await Stubs.start();
    memory = new MemoryClient(writeConfig(stubs.url));
    store = new AgentMemoryStore(memory, { sessionId: "lg" });
  });
  after(() => {
    memory.close(60_000);
    stubs.close();
  });

  test("LangGraph: a put remembers, a get returns the receipt, a search finds the memory", async () => {
    await store.put(ns("music"), "instrument", { text: "I play the harp at weddings." });
    const item = await store.get(ns("music"), "instrument");
    assert.ok(item);
    assert.equal(item.value.remembered, true);
    assert.equal(item.value.state, "applied");
    const hits = await store.search(ns(), { query: "harp at weddings", limit: 50 });
    const keys = new Set(hits.map((h) => h.key));
    assert.ok((item.value.facts as string[]).every((f) => keys.has(f)), "the remembered facts are found");
    assert.ok(hits.every((h) => h.namespace[0] === "agentmemory"));
    assert.deepEqual(await store.search(ns()), [], "AgentMemory recalls by meaning; there is no listing");
  });

  test("LangGraph: the same words again run nothing; other words replace the earlier ones", async () => {
    await store.put(ns(), "breakfast", { text: "I eat porridge for breakfast." });
    const first = (await store.get(ns(), "breakfast"))!.value;
    const before = (await stubs.seen()).writerCalls;
    await store.put(ns(), "breakfast", { text: "I eat porridge for breakfast." });
    assert.equal((await stubs.seen()).writerCalls, before, "the same words under the same key ran the writer again");
    assert.equal((await store.get(ns(), "breakfast"))!.value.operation_id, first.operation_id);
    await store.put(ns(), "breakfast", { content: "I eat pancakes for breakfast." });
    const second = (await store.get(ns(), "breakfast"))!.value;
    assert.notEqual(second.operation_id, first.operation_id);
    const found = new Set((await store.search(ns(), { query: "porridge pancakes breakfast", limit: 50 })).map((h) => h.key));
    assert.ok(!(first.facts as string[]).some((f) => found.has(f)), "the replaced words are still used");
    assert.ok((second.facts as string[]).every((f) => found.has(f)));
  });

  test("LangGraph: a delete forgets and the key reads as absent; putting it again is a new remember", async () => {
    await store.put(ns(), "pet", { text: "My parrot is called Kiwi." });
    const facts = (await store.get(ns(), "pet"))!.value.facts as string[];
    await store.delete(ns(), "pet");
    assert.equal(await store.get(ns(), "pet"), null);
    const found = new Set((await store.search(ns(), { query: "parrot Kiwi", limit: 50 })).map((h) => h.key));
    assert.ok(!facts.some((f) => found.has(f)));
    await store.put(ns(), "pet", { text: "My parrot is called Kiwi." });
    const again = await store.get(ns(), "pet");
    assert.ok(again && again.value.remembered === true);
  });

  test("LangGraph: words are required", async () => {
    await assert.rejects(store.put(ns(), "empty", { note: "no text" }), TypeError);
  });

  test("AI SDK: remember keeps the words and recall finds them, one line each", async () => {
    const tools = agentMemoryTools(memory, { sessionId: `ai-${RUN}`, maxFacts: 50, minScore: 0 });
    assert.ok("remember" in tools, "remember is offered by default");
    const kept = await (tools as any).remember.execute({ text: "I swim in the lake every Thursday." }, { ...toolOptions, toolCallId: `remember-${RUN}` });
    assert.deepEqual(kept, { state: "applied", remembered: true });
    const recalled = (await tools.recall.execute!({ question: "swim in the lake every Thursday" }, toolOptions)) as { found: number; memories: string[] };
    assert.ok(recalled.found > 0);
    assert.ok(recalled.memories.some((line) => /^fact .+: user said I swim in the lake every Thursday$/.test(line)), JSON.stringify(recalled.memories));
  });
});

describe("the adapters when the store cannot be read", () => {
  test("AI SDK: a recall that fails throws, never an empty answer", async () => {
    const memory = new MemoryClient({
      schemaVersion: 1, profile: "read_only", owner: { mode: "fixed", id: "o" },
      store: { uri: "bolt://127.0.0.1:9", username: "neo4j", password: "x", verifyConnectivity: false },
      embedding: { dimensions: 8, model: "m" },
    });
    try {
      const { recall } = agentMemoryTools(memory, { sessionId: "s", remember: false });
      // A text question with no embedding endpoint is refused: the model must hear that, not "nothing found".
      await assert.rejects(Promise.resolve(recall.execute!({ question: "where do I work?" }, toolOptions)), (error: Error) =>
        error instanceof MemoryUnavailableError && error.status === "invalid" && error.code === "embedding_required");
    } finally {
      memory.close();
    }
  });

  test("LangGraph: a search that fails throws StoreWriteError, never an empty list", async () => {
    const memory = new MemoryClient({
      schemaVersion: 1, profile: "read_only", owner: { mode: "fixed", id: "o" },
      store: { uri: "bolt://127.0.0.1:9", username: "neo4j", password: "x", verifyConnectivity: false },
      embedding: { dimensions: 8, model: "m" },
    });
    try {
      await assert.rejects(new AgentMemoryStore(memory).search(["user"], { query: "anything" }), StoreWriteError);
    } finally {
      memory.close();
    }
  });
});
