/**
 * AgentMemory as Vercel AI SDK tools (`npm install ai zod`).
 *
 *     import { MemoryClient } from "agentmemory-native";
 *     import { agentMemoryTools } from "agentmemory-native/ai";
 *
 *     const memory = new MemoryClient(config);
 *     const { text } = await generateText({ model, tools: agentMemoryTools(memory, { sessionId: "s1" }), prompt });
 *
 * `recall` returns the facts and preferences recall finds for a question, one line each, with ids. `remember` keeps
 * the person's own words through the library's journal; it is offered only for a context opened with a write profile
 * (explicit_write, learning). The owner is never taken from the model: it is the context's configured owner, or for a
 * per_request context the `owner` the application passes here. A failure (a timeout, an unreachable store, a refusal)
 * is thrown, so the model is told the memory could not be read, never that nothing is stored. The tool call's
 * abortSignal cancels the job in the library.
 */
import { tool } from "ai";
import { z } from "zod";
import type { MemoryClient } from "./client.js";
import type { ContextResult } from "./results.js";

export interface AgentMemoryToolsOptions {
  sessionId: string;
  /** For a context opened with per_request owners: whose memory. Never taken from the model. */
  owner?: string;
  maxFacts?: number;
  maxPreferences?: number;
  minScore?: number;
  /** Follow the people a recalled fact names to their own facts (RecallOptions.MaxConnectionFacts). */
  maxConnectionFacts?: number;
  /** Offer `remember` (it needs a write profile: explicit_write or learning). Default true; false for a read_only context. */
  remember?: boolean;
  /** Each call's own deadline in the library, in milliseconds (0: the context's default). */
  timeoutMs?: number;
}

/** A recall that did not run to completion; `status` and `code` say why. */
export class MemoryUnavailableError extends Error {
  override name = "MemoryUnavailableError";
  constructor(readonly status: string, readonly code: string, message = "") {
    super(`AgentMemory recall ${status}: ${code} ${message}`.trim());
  }
}

/** The recalled memories as the lines the model reads: facts first, then preferences, in recall's order. */
export function memoryLines(result: ContextResult): string[] {
  return [
    ...result.facts.map((f) => `fact ${f.id}: ${f.subject} ${f.predicate.replace(/_/g, " ")} ${f.object}`),
    ...result.preferences.map((p) => `preference ${p.id}: ${p.text}`),
  ];
}

function recallTool(client: MemoryClient, options: AgentMemoryToolsOptions) {
  return tool({
    description: "Look up what is remembered about the person: facts and preferences relevant to a question, one per line.",
    inputSchema: z.object({ question: z.string().min(1).describe("What to look up, in plain words") }),
    execute: async ({ question }: { question: string }, { abortSignal }: { abortSignal?: AbortSignal }) => {
      const result = await client.getContext({
        sessionId: options.sessionId, query: question, maxFacts: options.maxFacts ?? 5, maxPreferences: options.maxPreferences ?? 5,
        ...(options.owner !== undefined ? { owner: options.owner } : {}),
        ...(options.minScore !== undefined ? { minScore: options.minScore } : {}),
        ...(options.maxConnectionFacts ? { maxConnectionFacts: options.maxConnectionFacts } : {}),
        ...(options.timeoutMs ? { timeoutMs: options.timeoutMs } : {}),
        ...(abortSignal ? { signal: abortSignal } : {}),
      });
      if (result.status !== "ok") throw new MemoryUnavailableError(result.status, result.error?.code ?? "", result.error?.message);
      const lines = memoryLines(result);
      return { found: lines.length, memories: lines };
    },
  });
}

function rememberTool(client: MemoryClient, options: AgentMemoryToolsOptions) {
  return tool({
    description: "Remember something the person said about themselves, in their own words.",
    inputSchema: z.object({ text: z.string().min(1).describe("The person's own words to remember") }),
    execute: async ({ text }: { text: string }, { toolCallId, abortSignal }: { toolCallId: string; abortSignal?: AbortSignal }) => {
      const result = await client.remember({
        sourceId: `ai-sdk/${options.sessionId}/${toolCallId}`, sessionId: options.sessionId, text, occurredAt: new Date().toISOString(),
        ...(options.timeoutMs ? { timeoutMs: options.timeoutMs } : {}), ...(abortSignal ? { signal: abortSignal } : {}),
      });
      if (result.status !== "ok" || !result.receipt) {
        throw new MemoryUnavailableError(result.status, result.error?.code ?? "", result.error?.message);
      }
      return { state: result.receipt.state, remembered: result.receipt.state === "applied" && result.receipt.searchable === true };
    },
  });
}

export type RecallTool = ReturnType<typeof recallTool>;
export type RememberTool = ReturnType<typeof rememberTool>;

/** `recall`, and `remember` unless `remember: false` (a read-only context): each set exact, so it is an AI SDK ToolSet. */
export function agentMemoryTools(client: MemoryClient, options: AgentMemoryToolsOptions & { remember: false }): { recall: RecallTool };
export function agentMemoryTools(client: MemoryClient, options: AgentMemoryToolsOptions): { recall: RecallTool; remember: RememberTool };
export function agentMemoryTools(client: MemoryClient, options: AgentMemoryToolsOptions): { recall: RecallTool; remember?: RememberTool } {
  if (options.remember === false) return { recall: recallTool(client, options) };
  return { recall: recallTool(client, options), remember: rememberTool(client, options) };
}
