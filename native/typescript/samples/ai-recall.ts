/**
 * Sample: AgentMemory's recall as a Vercel AI SDK tool, driven by generateText. The model is scripted (ai/test's
 * MockLanguageModelV4), so the sample runs in the gate with no paid call: it asks for `recall`, then answers from what
 * the tool returned. Replace it with any AI SDK model.
 *
 * Tier B of the gate: the seeded read store and the stub embedder (native/gate/stubs.py).
 * AMP_URI=... AMP_PASSWORD=... STUB_URL=http://127.0.0.1:PORT node dist/samples/ai-recall.js
 */
import { generateText, stepCountIs } from "ai";
import { MockLanguageModelV4 } from "ai/test";
import { MemoryClient } from "../src/index.js";
import { agentMemoryTools } from "../src/ai.js";

const usage = {
  inputTokens: { total: 10, noCache: 10, cacheRead: 0, cacheWrite: 0 },
  outputTokens: { total: 5, text: 5, reasoning: 0 },
};

/** Step 1: call recall. Step 2: answer with the first memory the tool returned. */
const model = new MockLanguageModelV4({
  doGenerate: async (options) => {
    const toolResult = options.prompt.flatMap((m) => (m.role === "tool" ? m.content : [])).find((c) => c.type === "tool-result");
    if (!toolResult) {
      return {
        content: [{ type: "tool-call", toolCallId: "call-1", toolName: "recall", input: JSON.stringify({ question: "where do I work?" }) }],
        finishReason: { unified: "tool-calls", raw: "tool_calls" }, usage, warnings: [],
      };
    }
    const output = (toolResult as unknown as { output: { type: string; value: { memories: string[] } } }).output;
    return {
      content: [{ type: "text", text: `From memory: ${output.value.memories[0] ?? "nothing"}` }],
      finishReason: { unified: "stop", raw: "stop" }, usage, warnings: [],
    };
  },
});

export async function main(): Promise<string> {
  const memory = new MemoryClient({
    schemaVersion: 1, profile: "read_only", owner: { mode: "fixed", id: "gate-owner-a" },
    store: { uri: process.env.AMP_URI, username: process.env.AMP_USER ?? "neo4j", password: process.env.AMP_PASSWORD },
    embedding: { dimensions: 8, model: "gate-unit-axes", endpoint: { url: `${process.env.STUB_URL}/axes/v1/embeddings` } },
  });
  try {
    const { text, steps } = await generateText({
      model, tools: agentMemoryTools(memory, { sessionId: "sample", remember: false, minScore: 0 }), stopWhen: stepCountIs(3),
      prompt: "Where do I work?",
    });
    console.log(`steps: ${steps.length}; answer: ${text}`);
    return text;
  } finally {
    memory.close();
  }
}

if (process.argv[1]?.endsWith("ai-recall.js")) {
  main().then((text) => process.exit(/gate-fact-a-work: .*Zurich/.test(text) ? 0 : 1), (error) => { console.error(error); process.exit(1); });
}
