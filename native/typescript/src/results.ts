/** The library's result documents (native/CONTRACT.md), as the JSON says them. */

export interface ResultError {
  code: string;
  message?: string;
}

/** Every result: its status, its request's id and operation when there was one, an error when not ok. */
export interface ResultDocument {
  status: "ok" | "invalid" | "error" | "unavailable" | "timeout" | "cancelled" | string;
  requestId?: string;
  operation?: string;
  error?: ResultError;
  timing?: { queuedMs?: number; runMs?: number };
  [key: string]: unknown;
}

export interface Fact {
  id: string;
  subject: string;
  predicate: string;
  object: string;
  confidence: number;
  category?: string;
  validFrom?: string;
  validUntil?: string;
  createdAt: string;
  metadata?: Record<string, unknown>;
}

export interface Preference {
  id: string;
  category: string;
  text: string;
  context?: string;
  confidence: number;
  createdAt: string;
  metadata?: Record<string, unknown>;
}

export interface PastMessage {
  id: string;
  role?: string;
  text: string;
  at?: string;
}

export interface Entity {
  id: string;
  name: string;
  type?: string;
  description?: string;
}

export interface Relationship {
  id: string;
  source: string;
  type: string;
  target: string;
  description?: string;
}

export interface ContextResult extends ResultDocument {
  facts: Fact[];
  preferences: Preference[];
  windowFacts?: Fact[];
  messages?: PastMessage[];
  entities?: Entity[];
  relationships?: Relationship[];
  truncated?: boolean;
}

export interface Receipt {
  operationId: string;
  sourceId: string;
  state: "accepted" | "running" | "applied" | "partial" | "rejected" | "unknown" | "kept" | "forgotten" | "cancelled" | "erased" | string;
  duplicate?: boolean;
  searchable?: boolean;
  reason?: string;
  occurredAt: string;
  acceptedAt: string;
  startedAt?: string;
  finishedAt?: string;
  facts?: string[];
  preferences?: string[];
  closed?: string[];
  kind?: "remember" | "forget" | "erase" | string;
  targetOperationId?: string;
  deleted?: number;
  origin?: "explicit" | "observed" | string;
}

export interface WriteResult extends ResultDocument {
  receipt?: Receipt;
}

export interface CapabilitiesResult extends ResultDocument {
  profile: string;
  abi: { major: number; minor: number };
  operations: string[];
  features?: string[];
  embedding: { dimensions: number; generatesEmbeddings?: boolean; model?: string };
}

/** Whether a write's receipt says it was applied and can be recalled. */
export function remembered(receipt: Receipt | undefined): boolean {
  return receipt?.state === "applied" && receipt.searchable === true;
}

export interface CallOptions {
  /** The job's own deadline in the library (0: the context's default). */
  timeoutMs?: number;
  /** Cancels the job in the library; the promise then rejects with an AbortError. */
  signal?: AbortSignal;
}

export interface GetContextOptions extends CallOptions {
  sessionId: string;
  query?: string;
  queryVector?: ArrayLike<number>;
  owner?: string;
  maxFacts?: number;
  maxPreferences?: number;
  minScore?: number;
  requestId?: string;
  maxMessages?: number;
  maxEntities?: number;
  maxRelationships?: number;
  timeWindows?: boolean;
  maxConnectionFacts?: number;
}

export interface RememberOptions extends CallOptions {
  sourceId: string;
  sessionId: string;
  text: string;
  occurredAt: string;
  origin?: "explicit" | "observed";
  requestId?: string;
}
