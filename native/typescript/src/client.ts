/**
 * A context of the native library: open on construction, closed with close() (or `using`).
 *
 * Two layers, as in the Python binding. The low level (submit, Job.poll, cancel, release, state, waitAny) is
 * synchronous and maps one to one onto the C ABI; the gate runner drives it. The operations (getContext, remember, ...)
 * return promises: each job is admitted at once, and one waiter per context (am_v1_wait on koffi's worker threads)
 * settles them as they finish, so the event loop never blocks on the library. The waiter runs only while jobs are in
 * flight, so an idle client does not keep the process alive. An AbortSignal cancels the job in the library first.
 */
import { ABI_VERSION, Flags, PENDING, Status, TERMINAL } from "./abi.js";
import { JobAbortedError, NativeCallError, OpenError } from "./errors.js";
import { handle, hasWait, load, takeBuffer, type NativeFunctions } from "./library.js";
import type { CapabilitiesResult, ContextResult, GetContextOptions, RememberOptions, ResultDocument, WriteResult } from "./results.js";

const encoder = new TextEncoder();
const WAIT_SLICE_MS = 200;

function encode(document: unknown): Uint8Array {
  return encoder.encode(JSON.stringify(document));
}

/** The flags and revisions of am_v1_state. */
export interface ContextState {
  stateRevision: bigint;
  policyEpoch: bigint;
  deletionEpoch: bigint;
  memoryRevision: bigint;
  flags: number;
  readAllowed: boolean;
  admissionAllowed: boolean;
  writeQuiescent: boolean;
  closing: boolean;
  erasePending: boolean;
}

export interface OpenOptions {
  /** A library to load instead of AGENTMEMORY_NATIVE_LIBRARY or the platform package. */
  library?: string;
}

/** One submitted operation. Release it when done; a released handle is never reused. */
export class Job {
  private released = false;

  constructor(private readonly client: MemoryClient, readonly handle: bigint) {}

  /** The result document once the job has one, else null; waits at most `waitMs` inside the library. */
  poll(waitMs = 0): ResultDocument | null {
    const disposition: unknown[] = [PENDING];
    const pointer: unknown[] = [null];
    const length: unknown[] = [0];
    const fn = this.client.functions;
    const status = fn.am_v1_poll(this.client.handle, this.handle, Math.max(0, waitMs | 0), disposition, pointer, length);
    const data = takeBuffer(fn, pointer[0], length[0]);
    if (status !== Status.OK) throw new NativeCallError("am_v1_poll", status);
    if (disposition[0] !== TERMINAL || data === null) return null;
    return JSON.parse(data.toString("utf8")) as ResultDocument;
  }

  /** Asks the job to stop; its result then says "cancelled" unless it had already finished. */
  cancel(): void {
    const status = this.client.functions.am_v1_cancel(this.client.handle, this.handle);
    if (status !== Status.OK && status !== Status.INVALID_HANDLE) throw new NativeCallError("am_v1_cancel", status);
  }

  /** Gives up this handle. Idempotent; unfinished work stays the library's to finish. */
  release(): void {
    if (this.released) return;
    this.released = true;
    const status = this.client.functions.am_v1_job_release(this.client.handle, this.handle);
    if (status !== Status.OK && status !== Status.INVALID_HANDLE) throw new NativeCallError("am_v1_job_release", status);
  }

  /** Polls in this thread until the result arrives (blocking); the library's own deadline normally ends the job first. */
  wait(timeoutMs: number): ResultDocument {
    const deadline = Date.now() + timeoutMs;
    for (;;) {
      const result = this.poll(100);
      if (result !== null) return result;
      if (Date.now() > deadline) {
        this.cancel();
        throw new Error(`job ${this.handle} delivered no result within ${timeoutMs} ms`);
      }
    }
  }

  [Symbol.dispose](): void {
    this.release();
  }
}

interface InFlight {
  resolve(result: ResultDocument): void;
  reject(error: unknown): void;
  aborted: boolean;
}

export class MemoryClient {
  readonly functions: NativeFunctions;
  readonly defaultTimeoutMs: number;
  private context: bigint;
  private closed = false;
  private readonly inFlight = new Map<bigint, InFlight>();
  private waiting = false;

  constructor(config: Record<string, unknown>, options: OpenOptions = {}) {
    this.functions = load(options.library);
    const limits = (config.limits ?? {}) as Record<string, unknown>;
    // The library reads a JSON null as "absent"; so does this.
    this.defaultTimeoutMs = Number(limits.defaultTimeoutMs ?? 2000) || 2000;
    const data = encode(config);
    const context: unknown[] = [0];
    const pointer: unknown[] = [null];
    const length: unknown[] = [0];
    const status = this.functions.am_v1_open(ABI_VERSION, data, data.length, context, pointer, length);
    const error = takeBuffer(this.functions, pointer[0], length[0]);
    if (status !== Status.OK) throw new OpenError(status, error ? JSON.parse(error.toString("utf8")) : null);
    this.context = handle(context[0]);
  }

  /** The context handle, an opaque unsigned 64-bit value. */
  get handle(): bigint {
    return this.context;
  }

  // Low level -------------------------------------------------------------------------------------------------------

  /** Schedules one operation; throws NativeCallError when it is not admitted (OVERLOADED, CLOSING, ...). */
  submit(request: Record<string, unknown>, timeoutMs = 0): Job {
    const data = encode(request);
    const job: unknown[] = [0];
    const status = this.functions.am_v1_submit(this.context, data, data.length, Math.max(0, timeoutMs | 0), job);
    if (status !== Status.OK) throw new NativeCallError("am_v1_submit", status);
    return new Job(this, handle(job[0]));
  }

  /** Submits, waits for the result in this thread (blocking), and releases the job. */
  callSync(request: Record<string, unknown>, timeoutMs = 0): ResultDocument {
    const job = this.submit(request, timeoutMs);
    try {
      return job.wait((timeoutMs || this.defaultTimeoutMs) + 5000);
    } finally {
      job.release();
    }
  }

  state(): ContextState {
    const values: unknown[][] = [[0], [0], [0], [0]];
    const flags: unknown[] = [0];
    const status = this.functions.am_v1_state(this.context, values[0]!, values[1]!, values[2]!, values[3]!, flags);
    if (status !== Status.OK) throw new NativeCallError("am_v1_state", status);
    const f = Number(flags[0]);
    return {
      stateRevision: handle(values[0]![0]), policyEpoch: handle(values[1]![0]),
      deletionEpoch: handle(values[2]![0]), memoryRevision: handle(values[3]![0]), flags: f,
      readAllowed: (f & Flags.READ_ALLOWED) !== 0, admissionAllowed: (f & Flags.ADMISSION_ALLOWED) !== 0,
      writeQuiescent: (f & Flags.WRITE_QUIESCENT) !== 0, closing: (f & Flags.CLOSING) !== 0,
      erasePending: (f & Flags.ERASE_PENDING) !== 0,
    };
  }

  /** ABI 1.1: a finished job no wait has reported yet, waiting up to `waitMs` (blocking); 0n when none, or closing. */
  waitAny(waitMs = 0): bigint {
    if (!this.functions.am_v1_wait) throw new NativeCallError("am_v1_wait", Status.UNSUPPORTED_ABI, "this library implements ABI 1.0");
    const out: unknown[] = [0];
    const status = this.functions.am_v1_wait(this.context, Math.max(0, waitMs | 0), out) as number;
    if (status !== Status.OK) throw new NativeCallError("am_v1_wait", status);
    return handle(out[0]);
  }

  // Operations, as promises -----------------------------------------------------------------------------------------

  /** Submits, settles when the job finishes (or rejects with AbortError after the signal cancels it), then releases. */
  call(request: Record<string, unknown>, options: { timeoutMs?: number; signal?: AbortSignal } = {}): Promise<ResultDocument> {
    const { timeoutMs = 0, signal } = options;
    if (signal?.aborted) return Promise.reject(new JobAbortedError("aborted before it was submitted"));
    let job: Job;
    try {
      job = this.submit(request, timeoutMs);
    } catch (error) {
      return Promise.reject(error);
    }
    return new Promise<ResultDocument>((resolve, reject) => {
      const entry: InFlight = { resolve, reject, aborted: false };
      const onAbort = () => {
        entry.aborted = true;
        job.cancel(); // the result ("cancelled", or the one that won the race) still arrives, then the job is released
      };
      signal?.addEventListener("abort", onAbort, { once: true });
      const settle = (fn: () => void) => {
        signal?.removeEventListener("abort", onAbort);
        this.inFlight.delete(job.handle);
        job.release();
        fn();
      };
      this.inFlight.set(job.handle, {
        resolve: (result) => settle(() => (entry.aborted ? reject(new JobAbortedError("aborted")) : resolve(result))),
        reject: (error) => settle(() => reject(error)),
        aborted: false,
      });
      this.pump();
    });
  }

  /** The waiter: while jobs are in flight, one am_v1_wait at a time on a worker thread (a poll loop on ABI 1.0). */
  private pump(): void {
    if (this.waiting || this.inFlight.size === 0 || this.closed) return;
    this.waiting = true;
    const done = (finished: bigint | null, failure?: unknown) => {
      this.waiting = false;
      if (failure !== undefined) {
        for (const entry of [...this.inFlight.values()]) entry.reject(failure);
        return;
      }
      if (finished !== null) this.deliver(finished);
      this.pump();
    };
    if (hasWait(this.functions)) {
      const out: unknown[] = [0];
      this.functions.async.am_v1_wait!.async(this.context, WAIT_SLICE_MS, out, (error: unknown, status: number) => {
        if (error) return done(null, error);
        if (status !== Status.OK) return done(null, new NativeCallError("am_v1_wait", status));
        const job = handle(out[0]);
        done(job === 0n ? null : job);
      });
      return;
    }
    // ABI 1.0: poll the oldest job in flight on a worker thread, a slice at a time.
    const first = this.inFlight.keys().next().value as bigint;
    const disposition: unknown[] = [PENDING];
    const pointer: unknown[] = [null];
    const length: unknown[] = [0];
    this.functions.async.am_v1_poll.async(this.context, first, WAIT_SLICE_MS, disposition, pointer, length, (error: unknown, status: number) => {
      const data = takeBuffer(this.functions, pointer[0], length[0]);
      if (error) return done(null, error);
      if (status !== Status.OK) return done(null, new NativeCallError("am_v1_poll", status));
      if (disposition[0] === TERMINAL && data) this.inFlight.get(first)?.resolve(JSON.parse(data.toString("utf8")));
      done(null);
    });
  }

  private deliver(job: bigint): void {
    const entry = this.inFlight.get(job);
    if (!entry) return; // a job submitted with the low-level API, which its owner polls itself
    try {
      const result = new Job(this, job).poll(0);
      if (result !== null) entry.resolve(result);
    } catch (error) {
      entry.reject(error);
    }
  }

  capabilities(): Promise<CapabilitiesResult> {
    return this.call({ schemaVersion: 1, operation: "capabilities_get" }) as Promise<CapabilitiesResult>;
  }

  /** Owner-scoped recall of facts and preferences, and the sections asked for. */
  getContext(options: GetContextOptions): Promise<ContextResult> {
    return this.call(getContextRequest(options), options) as Promise<ContextResult>;
  }

  /** Remembers the owner's own words (explicit_write or learning profile), through the durable journal. */
  remember(options: RememberOptions): Promise<WriteResult> {
    const request: Record<string, unknown> = {
      schemaVersion: 1, operation: "memory_remember", sourceId: options.sourceId, sessionId: options.sessionId,
      text: options.text, occurredAt: options.occurredAt,
    };
    if (options.origin !== undefined) request.origin = options.origin;
    if (options.requestId !== undefined) request.requestId = options.requestId;
    return this.call(request, options) as Promise<WriteResult>;
  }

  /** The receipt of one write, by its operation id or its source id. */
  operation(ids: { operationId?: string; sourceId?: string; requestId?: string }, options: { timeoutMs?: number; signal?: AbortSignal } = {}): Promise<WriteResult> {
    return this.call({ schemaVersion: 1, operation: "memory_operation_get", ...ids }, options) as Promise<WriteResult>;
  }

  /** Ends a partial or unknown write by keeping what it wrote. */
  resolve(operationId: string, options: { requestId?: string; timeoutMs?: number; signal?: AbortSignal } = {}): Promise<WriteResult> {
    const request: Record<string, unknown> = { schemaVersion: 1, operation: "memory_operation_resolve", operationId, resolution: "keep" };
    if (options.requestId !== undefined) request.requestId = options.requestId;
    return this.call(request, options) as Promise<WriteResult>;
  }

  /** Stops using what one remember produced, or the owner's memories by id. */
  forget(target: { operationId?: string; memoryIds?: string[]; requestId?: string }, options: { timeoutMs?: number; signal?: AbortSignal } = {}): Promise<WriteResult> {
    return this.call({ schemaVersion: 1, operation: "memory_forget", ...target }, options) as Promise<WriteResult>;
  }

  /** Deletes everything of the configured owner and purges their words from the journal. Cannot be undone. */
  eraseOwner(confirm: boolean, options: { requestId?: string; timeoutMs?: number; signal?: AbortSignal } = {}): Promise<WriteResult> {
    const request: Record<string, unknown> = { schemaVersion: 1, operation: "memory_erase_owner", confirm };
    if (options.requestId !== undefined) request.requestId = options.requestId;
    return this.call(request, options) as Promise<WriteResult>;
  }

  /** Admin profile: what the store holds of AgentMemory's schema. Changes nothing. */
  schemaStatus(options: { timeoutMs?: number } = {}): Promise<ResultDocument> {
    return this.call({ schemaVersion: 1, operation: "schema_status" }, options);
  }

  /** Admin profile: creates what is missing of the schema and applies its migrations. */
  schemaBootstrap(options: { timeoutMs?: number } = {}): Promise<ResultDocument> {
    return this.call({ schemaVersion: 1, operation: "schema_bootstrap" }, { timeoutMs: options.timeoutMs ?? 120_000 });
  }

  // Lifetime --------------------------------------------------------------------------------------------------------

  /** Stops admission and waits (blocking) until running work has settled, then disposes the context. */
  close(timeoutMs = 10_000): void {
    if (this.closed) return;
    const deadline = Date.now() + timeoutMs;
    for (;;) {
      const status = this.functions.am_v1_close(this.context);
      if (status === Status.OK || status === Status.INVALID_HANDLE) break;
      if (status !== Status.BUSY) throw new NativeCallError("am_v1_close", status);
      if (Date.now() > deadline) throw new NativeCallError("am_v1_close", status, `work still running after ${timeoutMs} ms`);
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 10);
    }
    this.closed = true;
    for (const entry of [...this.inFlight.values()]) entry.reject(new NativeCallError("am_v1_poll", Status.INVALID_HANDLE, "the context closed"));
  }

  [Symbol.dispose](): void {
    this.close();
  }
}

/** A recall request, as the Python binding's get_context_request builds it. */
export function getContextRequest(options: GetContextOptions): Record<string, unknown> {
  const request: Record<string, unknown> = {
    schemaVersion: 1, operation: "memory_get_context", sessionId: options.sessionId,
    maxFacts: options.maxFacts ?? 5, maxPreferences: options.maxPreferences ?? 5,
  };
  if (options.query !== undefined) request.query = options.query;
  if (options.queryVector !== undefined) request.queryVector = Array.from(options.queryVector, Number);
  if (options.owner !== undefined) request.owner = options.owner;
  if (options.minScore !== undefined) request.minScore = options.minScore;
  if (options.requestId !== undefined) request.requestId = options.requestId;
  for (const key of ["maxMessages", "maxEntities", "maxRelationships", "timeWindows", "maxConnectionFacts"] as const) {
    if (options[key]) request[key] = options[key];
  }
  return request;
}
