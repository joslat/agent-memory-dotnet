/**
 * A LangGraph.js store over the AgentMemory native library (`npm install @langchain/langgraph-checkpoint`).
 *
 *     import { MemoryClient } from "agentmemory-native";
 *     import { AgentMemoryStore } from "agentmemory-native/langgraph";
 *
 *     const memory = new MemoryClient(config);   // "explicit_write" or "learning" for puts; "read_only" for search
 *     const store = new AgentMemoryStore(memory);
 *     await store.put(["user", "prefs"], "drink", { text: "I prefer tea to coffee." });
 *     const hits = await store.search(["user"], { query: "what do I like to drink?" });
 *
 * The same projection as the Python adapter (agentmemory_native/langgraph.py). AgentMemory is not a document store: a
 * put remembers the words in `value.text` (or `value.content`), which the library's writer turns into facts and
 * preferences of the context's one owner, through its durable journal. A get returns that remember's receipt, not the
 * words. A search returns the facts and preferences recall finds for the query (one item each, keyed by memory id), from
 * every key, because memories are the owner's, not the key's. A delete (put with null) forgets: the memories that key
 * produced are closed, not deleted. A put with other words under the same key forgets the earlier ones and remembers
 * the new words. The namespace and key only name the source; they never choose the owner.
 *
 * What it does not do, by design: a search without a query returns nothing (recall is by meaning; there is no listing),
 * filters and namespace prefixes do not narrow a search (memories are the owner's), and a get never returns the words
 * that were put.
 */
import { BaseStore, type GetOperation, type Item, type ListNamespacesOperation, type Operation, type OperationResults, type PutOperation, type SearchItem, type SearchOperation } from "@langchain/langgraph-checkpoint";
import type { MemoryClient } from "./client.js";
import type { Receipt, WriteResult } from "./results.js";

/** Receipt states whose memories are no longer used: a key in one of them reads as absent. */
const GONE = new Set(["rejected", "cancelled", "forgotten", "erased"]);

/** A put or delete was not accepted, or ended without its memories in use; `code` says why. */
export class StoreWriteError extends Error {
  override name = "StoreWriteError";
  constructor(readonly code: string, message = "") {
    super(`AgentMemory ${code}${message ? `: ${message}` : ""}`);
  }
}

export interface AgentMemoryStoreOptions {
  /** The session the store's remembers and recalls belong to. */
  sessionId?: string;
  /** How long a put waits for its write to end; past it the put returns and the write goes on (read it with get). */
  waitMs?: number;
  minScore?: number;
  /** A put whose write ends rejected throws StoreWriteError unless this is false. */
  raiseOnRejected?: boolean;
}

function time(value: string | undefined): Date {
  return value ? new Date(value) : new Date();
}

function failure(result: WriteResult | { status: string; error?: { code: string; message?: string } }): StoreWriteError {
  return new StoreWriteError(result.error?.code ?? result.status, result.error?.message ?? "");
}

export class AgentMemoryStore extends BaseStore {
  readonly sessionId: string;
  readonly waitMs: number;
  readonly minScore: number | undefined;
  readonly raiseOnRejected: boolean;

  constructor(readonly client: MemoryClient, options: AgentMemoryStoreOptions = {}) {
    super();
    this.sessionId = options.sessionId ?? "langgraph";
    this.waitMs = options.waitMs ?? 60_000;
    this.minScore = options.minScore;
    this.raiseOnRejected = options.raiseOnRejected ?? true;
  }

  /** One source per key and revision, so other words under the same key are a new source, never a conflict. */
  private static source(namespace: string[], key: string, revision: number): string {
    return `langgraph/${namespace.join("/")}/${key}/rev-${revision}`;
  }

  private async latest(namespace: string[], key: string): Promise<[number, Receipt] | null> {
    let found: [number, Receipt] | null = null;
    for (let revision = 1; ; revision++) {
      const result = await this.client.operation({ sourceId: AgentMemoryStore.source(namespace, key, revision) });
      if (result.status !== "ok" || !result.receipt) return found;
      found = [revision, result.receipt];
    }
  }

  private item(namespace: string[], key: string, receipt: Receipt): Item {
    return {
      value: {
        state: receipt.state, remembered: receipt.state === "applied" && receipt.searchable === true,
        facts: [...(receipt.facts ?? [])], preferences: [...(receipt.preferences ?? [])], operation_id: receipt.operationId,
      },
      key, namespace, createdAt: time(receipt.acceptedAt), updatedAt: time(receipt.finishedAt ?? receipt.acceptedAt),
    };
  }

  private async getItem(op: GetOperation): Promise<Item | null> {
    const latest = await this.latest(op.namespace, op.key);
    if (latest === null || GONE.has(latest[1].state)) return null;
    return this.item(op.namespace, op.key, latest[1]);
  }

  private async forgetReceipt(receipt: Receipt): Promise<void> {
    if (GONE.has(receipt.state)) return;
    const result = await this.client.forget({ operationId: receipt.operationId }, { timeoutMs: this.waitMs });
    if (result.status !== "ok") throw failure(result);
  }

  private async putItem(op: PutOperation): Promise<void> {
    const latest = await this.latest(op.namespace, op.key);
    if (op.value === null) {
      if (latest !== null) await this.forgetReceipt(latest[1]);
      return;
    }
    const text = op.value.text ?? op.value.content;
    if (typeof text !== "string" || !text.trim()) {
      throw new TypeError('AgentMemoryStore remembers words: put a value with a non-empty "text" (or "content").');
    }
    const now = new Date().toISOString();
    let revision: number;
    let said: string;
    if (latest === null) [revision, said] = [1, now];
    // Forgotten or erased: putting it again is a new remember, never the old receipt.
    else if (GONE.has(latest[1].state)) [revision, said] = [latest[0] + 1, now];
    // The key's current words keep their original time, so the same words again are the same source (the library returns
    // its receipt and runs nothing); other words are refused as a conflict, handled below.
    else [revision, said] = [latest[0], latest[1].occurredAt];
    let result = await this.client.remember({
      sourceId: AgentMemoryStore.source(op.namespace, op.key, revision), sessionId: this.sessionId, text, occurredAt: said,
      timeoutMs: this.waitMs,
    });
    if (result.status !== "ok" && result.error?.code === "source_conflict" && latest !== null) {
      // Other words under the same key: stop using the earlier ones, then remember these as the next revision.
      await this.forgetReceipt(latest[1]);
      result = await this.client.remember({
        sourceId: AgentMemoryStore.source(op.namespace, op.key, revision + 1), sessionId: this.sessionId, text, occurredAt: now,
        timeoutMs: this.waitMs,
      });
    }
    if (result.status !== "ok" || !result.receipt) throw failure(result);
    if (result.receipt.state === "rejected" && this.raiseOnRejected) throw new StoreWriteError("rejected", result.receipt.reason ?? "");
  }

  private async searchItems(op: SearchOperation): Promise<SearchItem[]> {
    // AgentMemory recalls by meaning; it has no listing of every memory.
    if (!op.query) return [];
    const limit = op.limit ?? 10;
    const offset = op.offset ?? 0;
    const count = Math.max(1, limit + offset);
    const result = await this.client.getContext({
      sessionId: this.sessionId, query: op.query, maxFacts: Math.min(count, 50), maxPreferences: Math.min(count, 50),
      ...(this.minScore !== undefined ? { minScore: this.minScore } : {}),
    });
    if (result.status !== "ok") throw failure(result);
    const items: SearchItem[] = [];
    for (const fact of result.facts) {
      items.push({
        namespace: ["agentmemory", "facts"], key: fact.id,
        value: {
          kind: "fact", text: `${fact.subject} ${fact.predicate.replace(/_/g, " ")} ${fact.object}`, subject: fact.subject,
          predicate: fact.predicate, object: fact.object, confidence: fact.confidence, valid_from: fact.validFrom ?? null,
          valid_until: fact.validUntil ?? null,
        },
        createdAt: time(fact.createdAt), updatedAt: time(fact.createdAt),
      });
    }
    for (const preference of result.preferences) {
      items.push({
        namespace: ["agentmemory", "preferences"], key: preference.id,
        value: { kind: "preference", text: preference.text, category: preference.category, confidence: preference.confidence },
        createdAt: time(preference.createdAt), updatedAt: time(preference.createdAt),
      });
    }
    return items.slice(offset, offset + limit);
  }

  async batch<Op extends Operation[]>(operations: Op): Promise<OperationResults<Op>> {
    const results: unknown[] = [];
    for (const op of operations) {
      if ("value" in op) {
        await this.putItem(op as PutOperation);
        results.push(undefined);
      } else if ("key" in op) {
        results.push(await this.getItem(op as GetOperation));
      } else if ("namespacePrefix" in op) {
        results.push(await this.searchItems(op as SearchOperation));
      } else if ("limit" in op && "offset" in op) {
        // Search results live in these two namespaces; puts may use any namespace as a source label.
        const list = op as ListNamespacesOperation;
        results.push([["agentmemory", "facts"], ["agentmemory", "preferences"]].slice(list.offset, list.offset + list.limit));
      } else {
        throw new Error("AgentMemoryStore does not support this operation");
      }
    }
    return results as OperationResults<Op>;
  }
}
