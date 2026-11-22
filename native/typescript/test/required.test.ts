/**
 * The TypeScript binding's own tests of native/gate/REQUIRED.md, section 1: what the shared cases cannot say.
 * node --test dist/test/. Tier A needs only the library; GATE_TIER=B (with AMP_URI, AMP_USER, AMP_PASSWORD) adds the
 * ones that recall from the seeded store.
 */
import assert from "node:assert/strict";
import { copyFileSync, existsSync, mkdtempSync, readFileSync, realpathSync, writeFileSync } from "node:fs";
import { arch, platform, tmpdir } from "node:os";
import { join } from "node:path";
import { describe, test } from "node:test";
import { Worker } from "node:worker_threads";
import koffi from "koffi";
import { JobAbortedError, MemoryClient, NativeCallError, NativeLibraryError, OpenError, Status, load, resolveLibrary, verifyAgainstManifest } from "../src/index.js";
import { pinLibrary } from "../src/library.js";

const TIER = (process.env.GATE_TIER ?? "A").toUpperCase();
const STORE = TIER !== "A";
const NEAR_AXIS_0 = [1.0, 0.1, 0, 0, 0, 0, 0, 0];
const A_IDS = ["gate-fact-a-colour", "gate-fact-a-work", "gate-pref-a-concise"];

function config(limits?: Record<string, unknown>): Record<string, unknown> {
  return {
    schemaVersion: 1, profile: "read_only", owner: { mode: "fixed", id: "gate-owner-a" },
    store: {
      uri: STORE ? process.env.AMP_URI : "bolt://127.0.0.1:9", username: process.env.AMP_USER ?? "neo4j",
      password: process.env.AMP_PASSWORD ?? "unused", verifyConnectivity: STORE,
    },
    embedding: { dimensions: 8, model: "gate-unit-axes" },
    ...(limits ? { limits } : {}),
  };
}

const ids = (r: { facts: { id: string }[]; preferences: { id: string }[] }) => [...r.facts.map((f) => f.id), ...r.preferences.map((p) => p.id)].sort();

describe("REQUIRED.md: the binding's loader", () => {
  test("R-LOAD-PATH: an explicit path or AGENTMEMORY_NATIVE_LIBRARY, and nowhere else", () => {
    const explicit = process.env.AGENTMEMORY_NATIVE_LIBRARY;
    if (!explicit) return; // a packaged run: R-PACKAGE covers where the library comes from
    assert.equal(resolveLibrary().manifest, null);
    assert.throws(() => resolveLibrary(join(tmpdir(), "no-such-library.so")), NativeLibraryError);
  });

  test("R-LOAD-UNLOADABLE: a file that cannot be loaded is refused with an actionable error", () => {
    const folder = mkdtempSync(join(tmpdir(), "am-ts-"));
    const fake = join(folder, platform() === "win32" ? "agentmemory_native.dll" : "agentmemory_native.so");
    writeFileSync(fake, "\x7fELF but not really a library");
    assert.throws(() => load(fake), (error: Error) => {
      assert.ok(error instanceof NativeLibraryError);
      assert.match(error.message, /agentmemory_native/);
      assert.match(error.message, new RegExp(arch()));
      assert.match(error.message, /built for this platform/);
      return true;
    });
  });

  test("R-LOAD-FOREIGN: a loadable library without the exports is refused", () => {
    const other = platform() === "win32" ? join(process.env.SystemRoot ?? "C:\\Windows", "System32", "kernel32.dll")
      : platform() === "darwin" ? undefined : ["/lib/x86_64-linux-gnu/libm.so.6", "/lib/aarch64-linux-gnu/libm.so.6", "/usr/lib64/libm.so.6"].find(existsSync);
    if (!other) return; // macOS keeps its system libraries in the dyld cache, not as files
    assert.throws(() => load(other), /not an AgentMemory native library/);
  });

  test("R-PACKAGE: a library whose bytes are not the manifest's is refused before it is loaded", () => {
    const folder = mkdtempSync(join(tmpdir(), "am-ts-"));
    const damaged = join(folder, "library");
    writeFileSync(damaged, "not the bytes");
    assert.throws(() => verifyAgainstManifest(damaged, { abi: { major: 1, minor: 1 }, platformTag: "x", library: { file: "library", size: 13, sha256: "0".repeat(64) } }),
      /reinstall agentmemory-native/);
  });

  test("R-FREE-NULL: freeing a null buffer is a no-op", () => {
    load().am_v1_buffer_free(null);
  });
});

/**
 * A copy of a small system library to load and unload, and how to see whether a file is loaded in this process. Not the
 * AgentMemory library: a second copy of it would start a second .NET runtime, whose threads outliving an unload is the
 * crash this guards against. Undefined where there is no cheap way to look.
 */
function unloadProbe(): { source: string; extension: string; isLoaded: (path: string) => boolean } | undefined {
  if (platform() === "linux") {
    const triplet = `${arch() === "arm64" ? "aarch64" : "x86_64"}-linux-gnu`;
    const source = [`/lib/${triplet}`, `/usr/lib/${triplet}`, "/lib64", "/usr/lib64", "/lib", "/usr/lib"]
      .map((folder) => join(folder, "libz.so.1")).find(existsSync);
    return source ? { source, extension: ".so", isLoaded: (path) => readFileSync("/proc/self/maps", "utf8").includes(path) } : undefined;
  }
  if (platform() === "win32") {
    const source = join(process.env.SystemRoot ?? "C:\\Windows", "System32", "version.dll");
    if (!existsSync(source)) return undefined;
    const getModuleHandle = koffi.load("kernel32.dll").func("void *__stdcall GetModuleHandleW(const char16_t *)");
    return { source, extension: ".dll", isLoaded: (path) => getModuleHandle(path) !== null };
  }
  return undefined;
}

const PROBE = unloadProbe();

describe("REQUIRED.md: the library is never unloaded", () => {
  test("R-NEVER-UNLOAD: a pinned library stays loaded when koffi unloads it; an unpinned one does not",
    { skip: !PROBE && `no small system library to copy and no cheap look at what is loaded on ${platform()} ${arch()}` }, () => {
      const probe = PROBE!;
      const folder = mkdtempSync(join(realpathSync(tmpdir()), "am-ts-pin-"));
      const control = join(folder, `control${probe.extension}`);
      const pinned = join(folder, `pinned${probe.extension}`);
      copyFileSync(probe.source, control);
      copyFileSync(probe.source, pinned);

      const unpinnedLibrary = koffi.load(control);
      assert.ok(probe.isLoaded(control), "the control copy is loaded");
      unpinnedLibrary.unload();
      assert.ok(!probe.isLoaded(control), "koffi unloads a library it loaded (else the check below would prove nothing)");

      const pinnedLibrary = koffi.load(pinned);
      assert.equal(pinLibrary(pinned), true, "the pin is taken");
      pinnedLibrary.unload();
      assert.ok(probe.isLoaded(pinned), "the pinned copy is still loaded after koffi unloaded it");
    });

  test("R-NEVER-UNLOAD: the binding pins the library it loads", () => {
    assert.equal(load().pinned, true);
  });
});

describe("REQUIRED.md: handles, state and the waiter", () => {
  test("R-HANDLES: context and job handles are unsigned 64-bit BigInts", async () => {
    const memory = new MemoryClient(config());
    try {
      assert.equal(typeof memory.handle, "bigint");
      assert.ok(memory.handle > 0n && memory.handle < 2n ** 64n);
      const job = memory.submit({ schemaVersion: 1, operation: "capabilities_get" });
      assert.equal(typeof job.handle, "bigint");
      assert.equal(job.wait(10_000).status, "ok");
      job.release();
      console.log(`METRIC context_handle_bits=${memory.handle.toString(2).length} job_handle_bits=${job.handle.toString(2).length}`);
    } finally {
      memory.close();
    }
  });

  test("B15: am_v1_state with a null output is refused and still clears the others", () => {
    const fn = load();
    const memory = new MemoryClient(config());
    try {
      const values: unknown[][] = [[0xdead], [0xdead], [0xdead], [0xdead]];
      const status = fn.am_v1_state(memory.handle, values[0]!, values[1]!, values[2]!, values[3]!, null as unknown as unknown[]);
      assert.equal(status, Status.INVALID_ARGUMENT);
      assert.deepEqual(values.map((v) => Number(v[0])), [0, 0, 0, 0]);
    } finally {
      memory.close();
    }
  });

  test("R-WAITER-CLOSE: a thread blocked in am_v1_wait is woken by close, within seconds, with no job", { timeout: 60_000 }, async () => {
    const memory = new MemoryClient(config());
    const worker = new Worker(new URL("./waiter-worker.js", import.meta.url), {
      workerData: { library: load().path, context: memory.handle.toString() },
    });
    // Listened for from the start: when the worker ends before its last message is handled, Node delivers that message
    // and then 'exit' in one callback, before a listener attached after the message could see it.
    const exited = new Promise((resolve) => worker.once("exit", resolve));
    const messages: Array<{ kind: string; status?: number; job?: string; ms?: number }> = [];
    const woken = new Promise<{ status?: number; job?: string; ms?: number }>((resolve, reject) => {
      worker.on("message", (message) => {
        messages.push(message);
        if (message.kind === "woken") resolve(message);
      });
      worker.once("error", reject);
    });
    await new Promise((r) => setTimeout(r, 300));
    assert.deepEqual(messages.map((m) => m.kind), ["waiting"], "the worker is blocked in am_v1_wait");
    memory.close();
    const result = await woken;
    // The worker ends on its own, its script done: terminate() stopped it while koffi and the library were still set up
    // in its isolate, and the process then failed as it exited, about one run in thirty (SIGSEGV, Linux).
    await exited;
    assert.ok([Status.OK, Status.INVALID_HANDLE].includes(result.status as 0 | 3), `status ${result.status}`);
    assert.equal(result.job, "0");
    assert.ok((result.ms ?? 99_999) < 5000, `the waiter was woken after ${result.ms} ms`);
  });
});

describe("REQUIRED.md: async calls on the store", { skip: !STORE && "tier A: needs the seeded store" }, () => {
  test("R-ASYNC: recall, 16 at once, and an aborted call rejects with AbortError", async () => {
    const memory = new MemoryClient(config());
    try {
      const ok = await memory.getContext({ sessionId: "s", queryVector: NEAR_AXIS_0, minScore: 0 });
      assert.deepEqual(ids(ok), A_IDS);
      const many = await Promise.all(Array.from({ length: 16 }, (_, i) => memory.getContext({ sessionId: `s${i}`, queryVector: NEAR_AXIS_0, minScore: 0 })));
      assert.ok(many.every((r) => JSON.stringify(ids(r)) === JSON.stringify(A_IDS)));
      const controller = new AbortController();
      const call = memory.getContext({ sessionId: "c", queryVector: NEAR_AXIS_0, timeoutMs: 5000, signal: controller.signal });
      controller.abort();
      await assert.rejects(call, JobAbortedError);
    } finally {
      memory.close();
    }
  });

  test("B7: 300 calls aborted at any point leak no job, and capacity comes back", async () => {
    const memory = new MemoryClient(config({ maxRunningJobs: 1, maxPendingJobs: 1 }));
    try {
      for (let i = 0; i < 300; i++) {
        const controller = new AbortController();
        const call = memory.getContext({ sessionId: "c", queryVector: NEAR_AXIS_0, timeoutMs: 5000, signal: controller.signal });
        for (let k = 0; k < i % 4; k++) await Promise.resolve();
        controller.abort();
        try {
          await call;
        } catch (error) {
          if (error instanceof NativeCallError) assert.equal(error.status, Status.OVERLOADED, "only backpressure may refuse a submit here");
          else assert.ok(error instanceof JobAbortedError, String(error));
        }
      }
      for (let attempt = 0; attempt < 100; attempt++) {
        try {
          const after = await memory.getContext({ sessionId: "after", queryVector: NEAR_AXIS_0, minScore: 0 });
          assert.deepEqual(ids(after), A_IDS);
          return;
        } catch (error) {
          assert.ok(error instanceof NativeCallError && error.status === Status.OVERLOADED);
          await new Promise((r) => setTimeout(r, 100));
        }
      }
      assert.fail("capacity never came back: jobs leaked");
    } finally {
      memory.close(30_000);
    }
  });

  test("B8: an open cannot be cancelled in this binding; a refused second open is reported and the store is free after close", () => {
    // MemoryClient opens synchronously, so there is no cancelled open to leak. What B8 protects is that a context is
    // never left holding the store: one context per store, until it closes.
    const first = new MemoryClient(config());
    assert.throws(() => new MemoryClient(config()), (error: Error) => error instanceof OpenError && error.code === "authority_held");
    first.close();
    new MemoryClient(config()).close();
  });

  test("R-MEMORY: 500 recalls do not grow the process by 64 MiB", async () => {
    const memory = new MemoryClient(config());
    try {
      for (let i = 0; i < 50; i++) await memory.getContext({ sessionId: "warm", queryVector: NEAR_AXIS_0, minScore: 0 });
      const before = process.memoryUsage().rss;
      const latencies: number[] = [];
      for (let i = 0; i < 500; i++) {
        const start = performance.now();
        const result = await memory.getContext({ sessionId: `m${i % 7}`, queryVector: NEAR_AXIS_0, minScore: 0 });
        latencies.push(performance.now() - start);
        assert.deepEqual(ids(result), A_IDS);
      }
      const growth = process.memoryUsage().rss - before;
      latencies.sort((a, b) => a - b);
      console.log(`METRIC recall_p50_ms=${latencies[250]!.toFixed(2)} recall_p95_ms=${latencies[474]!.toFixed(2)} rss_growth_kib=${Math.round(growth / 1024)}`);
      assert.ok(growth < 64 * 1024 * 1024, `grew by ${Math.round(growth / 1024)} KiB`);
    } finally {
      memory.close();
    }
  });
});
