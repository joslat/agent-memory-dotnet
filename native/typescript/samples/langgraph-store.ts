/**
 * Sample: AgentMemory as a LangGraph.js store. A put remembers the person's words, a search recalls what the writer kept,
 * a delete forgets. Any LangGraph graph can take the store (`graph.compile({ store })`); this sample calls it directly.
 *
 * Tier C of the gate: the write store and the stub model (native/gate/stubs.py).
 * AMP_URI=... AMP_PASSWORD=... STUB_URL=http://127.0.0.1:PORT node dist/samples/langgraph-store.js
 */
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { MemoryClient } from "../src/index.js";
import { AgentMemoryStore } from "../src/langgraph.js";

export async function main(): Promise<boolean> {
  const stub = process.env.STUB_URL;
  const memory = new MemoryClient({
    schemaVersion: 1, profile: "explicit_write", owner: { mode: "fixed", id: "gate-writer-owner" },
    store: { uri: process.env.AMP_URI, username: process.env.AMP_USER ?? "neo4j", password: process.env.AMP_PASSWORD },
    embedding: { dimensions: 8, model: "stub-embedder", endpoint: { url: `${stub}/letters/v1/embeddings` } },
    writer: { model: "stub-writer", endpoint: { url: `${stub}/v1/chat/completions` } },
    state: { directory: join(mkdtempSync(join(tmpdir(), "am-sample-")), "state") },
  });
  const store = new AgentMemoryStore(memory, { sessionId: "sample" });
  const namespace = ["user", `sample-${Math.random().toString(16).slice(2, 8)}`];
  try {
    await store.put(namespace, "hobby", { text: "I paint watercolours on Sundays." });
    const receipt = await store.get(namespace, "hobby");
    console.log("put:", receipt?.value.state, receipt?.value.facts);
    const hits = await store.search(namespace, { query: "paint watercolours on Sundays", limit: 5 });
    console.log("search:", hits.map((h) => `${h.namespace.join("/")} ${h.value.text}`));
    await store.delete(namespace, "hobby");
    const gone = await store.get(namespace, "hobby");
    console.log("after delete:", gone);
    return receipt?.value.remembered === true && hits.some((h) => (receipt.value.facts as string[]).includes(h.key)) && gone === null;
  } finally {
    memory.close(60_000);
  }
}

if (process.argv[1]?.endsWith("langgraph-store.js")) {
  main().then((ok) => process.exit(ok ? 0 : 1), (error) => { console.error(error); process.exit(1); });
}
