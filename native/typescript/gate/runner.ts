/**
 * The TypeScript runner of the shared native gate (native/gate/cases/FORMAT.md), on the binding's low-level functions.
 *
 * node dist/gate/runner.js --tier A|B|C. Environment as the gate's: AGENTMEMORY_NATIVE_LIBRARY (or the platform
 * package), AMP_URI, AMP_USER, AMP_PASSWORD, AMP_TLS_URI, AMP_TLS_OTHER_NAME_URI; PYTHON for the stubs (python3/python).
 */
import { spawn, type ChildProcess } from "node:child_process";
import { mkdtempSync, readFileSync, readdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve as resolvePath } from "node:path";
import { createInterface } from "node:readline";
import { fileURLToPath } from "node:url";
import { ABI_VERSION, Flags, PENDING, Status, TERMINAL, statusName } from "../src/abi.js";
import { handle, hasWait, load, takeBuffer, type NativeFunctions } from "../src/library.js";

const HERE = dirname(fileURLToPath(import.meta.url));
const GATE = resolvePath(HERE, "..", "..", "..", "gate"); // dist/gate -> native/gate
const CASES = join(GATE, "cases");
const UNREACHABLE = "bolt://127.0.0.1:9";
const encoder = new TextEncoder();

type Json = any;
export interface Case {
  id: string;
  tier: string;
  source: string;
  stubs?: boolean;
  needs?: string[];
  steps: Json[];
  file?: string;
}

class CaseFailure extends Error {}
class Skip extends Error {}

const MISSING = Symbol("missing");

export function loadCases(tier?: string): Case[] {
  const found: Case[] = [];
  for (const name of readdirSync(CASES).filter((n) => n.endsWith(".json") && n !== "fixtures.json").sort()) {
    const document = JSON.parse(readFileSync(join(CASES, name), "utf8"));
    for (const c of document.cases) found.push({ ...c, file: name });
  }
  const wanted: Record<string, string[]> = { A: ["A"], B: ["A", "B"], C: ["C"] };
  const tiers = tier ? wanted[tier] ?? [] : ["A", "B", "C"];
  return found.filter((c) => tiers.includes(c.tier));
}

export function fixtures(): Json {
  return JSON.parse(readFileSync(join(CASES, "fixtures.json"), "utf8"));
}

export class Environment {
  readonly vars: Record<string, Json>;

  constructor(readonly tier: string, stubUrl: string | null) {
    const store = tier !== "A";
    this.vars = {
      tier,
      storeUri: store ? process.env.AMP_URI ?? UNREACHABLE : UNREACHABLE,
      storeUser: process.env.AMP_USER ?? "neo4j",
      storePassword: process.env.AMP_PASSWORD ?? "gate-tier-a-unused",
      verify: store,
      tlsUri: process.env.AMP_TLS_URI ?? null,
      tlsOtherNameUri: process.env.AMP_TLS_OTHER_NAME_URI ?? null,
      stubUrl,
    };
  }

  freshState(): string {
    return join(mkdtempSync(join(tmpdir(), "am-gate-state-")), "state");
  }
}

// Pointers and placeholders ------------------------------------------------------------------------------------------
function split(pointer: string): string[] {
  if (pointer === "" || pointer === "/") return [];
  return pointer.replace(/^\//, "").split("/").map((p) => p.replace(/~1/g, "/").replace(/~0/g, "~"));
}

function getPointer(document: Json, pointer: string): Json {
  let target = document;
  for (const part of split(pointer)) {
    if (Array.isArray(target)) {
      if (!/^\d+$/.test(part) || Number(part) >= target.length) return MISSING;
      target = target[Number(part)];
    } else if (target !== null && typeof target === "object") {
      if (!(part in target)) return MISSING;
      target = target[part];
    } else return MISSING;
  }
  return target;
}

function setPointer(document: Json, pointer: string, value: Json): void {
  const parts = split(pointer);
  let target = document;
  for (const part of parts.slice(0, -1)) {
    if (Array.isArray(target)) target = target[Number(part)];
    else target = target[part] ??= {};
  }
  target[parts[parts.length - 1]!] = value;
}

function removePointer(document: Json, pointer: string): void {
  const parts = split(pointer);
  let target = document;
  for (const part of parts.slice(0, -1)) target = target[part];
  delete target[parts[parts.length - 1]!];
}

function resolveValue(value: Json, env: Environment, fx: Json, vars: Record<string, Json>): Json {
  if (Array.isArray(value)) return value.map((v) => resolveValue(v, env, fx, vars));
  if (value === null || typeof value !== "object") return value;
  if ("$var" in value) {
    const [name, ...rest] = String(value.$var).split("/");
    const pointer = rest.length ? "/" + rest.join("/") : "";
    if (name! in vars) return pointer ? getPointer(vars[name!], pointer) : vars[name!];
    if (!(name! in env.vars)) throw new Error(`unknown variable ${name}`);
    const found = env.vars[name!];
    if (found === null || found === undefined) throw new Skip(`needs ${name}, which this run does not have`);
    return found;
  }
  if ("$freshState" in value) return env.freshState();
  if ("$vector" in value) return [...fx.vectors[value.$vector]];
  if ("$config" in value) {
    const document = resolveValue(structuredClone(fx.configs[value.$config]), env, fx, vars);
    for (const [pointer, replacement] of Object.entries(value.$with ?? {})) setPointer(document, pointer, resolveValue(replacement, env, fx, vars));
    for (const pointer of value.$without ?? []) removePointer(document, pointer);
    return document;
  }
  if ("$concat" in value) return value.$concat.map((p: Json) => String(resolveValue(p, env, fx, vars))).join("");
  return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, resolveValue(v, env, fx, vars)]));
}

/** JSON with keys kept in insertion order and BigInt as numbers, like Python's json.dumps. */
function toJson(value: Json): string {
  return JSON.stringify(value, (_, v) => (typeof v === "bigint" ? Number(v) : v));
}

function rawBytes(spec: Json, env: Environment, fx: Json, vars: Record<string, Json>): Uint8Array {
  if (spec && typeof spec === "object" && !Array.isArray(spec)) {
    if ("$base64" in spec) return new Uint8Array(Buffer.from(spec.$base64, "base64"));
    if ("$utf8" in spec) return encoder.encode(spec.$utf8);
    if ("$repeat" in spec) return encoder.encode(String(spec.$repeat[0]).repeat(spec.$repeat[1]));
    if ("$jsonReplace" in spec) {
      const [document, old, replacement] = spec.$jsonReplace;
      return encoder.encode(pythonJson(resolveValue(document, env, fx, vars)).replace(String(resolveValue(old, env, fx, vars)), replacement));
    }
  }
  return encoder.encode(toJson(resolveValue(spec, env, fx, vars)));
}

/** Python's json.dumps spacing (", " and ": "), for $jsonReplace's text to match the reference runner's. */
function pythonJson(value: Json): string {
  if (Array.isArray(value)) return "[" + value.map(pythonJson).join(", ") + "]";
  if (value !== null && typeof value === "object") return "{" + Object.entries(value).map(([k, v]) => JSON.stringify(k) + ": " + pythonJson(v)).join(", ") + "}";
  return JSON.stringify(value);
}

// Matching ------------------------------------------------------------------------------------------------------------
function recalledIds(result: Json): string[] {
  if (!result || typeof result !== "object") return [];
  const ids = new Set<string>([...(result.facts ?? []).map((f: Json) => f.id), ...(result.preferences ?? []).map((p: Json) => p.id)]);
  return [...ids].sort();
}

function short(value: Json): string {
  const text = value === MISSING ? "(missing)" : toJson(value);
  return text.length <= 300 ? text : text.slice(0, 297) + "...";
}

const isOperatorObject = (e: Json) =>
  e !== null && typeof e === "object" && !Array.isArray(e) && Object.keys(e).length > 0 && Object.keys(e).every((k) => k.startsWith("$") && k !== "$ids");

function matches(actual: Json, expected: Json): boolean {
  try {
    match(actual, expected);
    return true;
  } catch (e) {
    if (e instanceof CaseFailure) return false;
    throw e;
  }
}

function sortedJson(list: Json[]): string[] {
  return list.map((x) => toJson(x)).sort();
}

function operate(actual: Json, operator: string, operand: Json, where: string): void {
  const fail = (text: string): never => {
    throw new CaseFailure(`${where || "/"}: ${text}, got ${short(actual)}`);
  };
  const items = Array.isArray(operand) ? operand : [operand];
  switch (operator) {
    case "$oneOf":
      if (!operand.some((o: Json) => matches(actual, o))) fail(`expected one of ${short(operand)}`);
      return;
    case "$not":
      if (matches(actual, operand)) fail(`expected anything but ${short(operand)}`);
      return;
    case "$contains":
      for (const item of items) {
        if (typeof actual === "string") { if (!actual.includes(item)) fail(`expected text containing ${short(item)}`); }
        else if (Array.isArray(actual)) { if (!actual.some((a) => matches(a, item))) fail(`expected a list containing ${short(item)}`); }
        else fail("expected text or a list");
      }
      return;
    case "$notContains":
      for (const item of items) {
        if ((typeof actual === "string" && actual.includes(item)) || (Array.isArray(actual) && actual.some((a) => matches(a, item)))) {
          fail(`expected nothing containing ${short(item)}`);
        }
      }
      return;
    case "$setEq":
      if (!Array.isArray(actual) || toJson(sortedJson(actual)) !== toJson(sortedJson(operand))) fail(`expected the set ${short(operand)}`);
      return;
    case "$gt": if (typeof actual !== "number" || !(actual > operand)) fail(`expected more than ${operand}`); return;
    case "$gte": if (typeof actual !== "number" || !(actual >= operand)) fail(`expected at least ${operand}`); return;
    case "$lt": if (typeof actual !== "number" || !(actual < operand)) fail(`expected less than ${operand}`); return;
    case "$len": if (actual === null || actual === undefined || (actual.length ?? Object.keys(actual).length) !== operand) fail(`expected length ${operand}`); return;
    case "$type": {
      const ok: Record<string, (v: Json) => boolean> = {
        string: (v) => typeof v === "string", number: (v) => typeof v === "number", integer: (v) => Number.isInteger(v),
        boolean: (v) => typeof v === "boolean", object: (v) => v !== null && typeof v === "object" && !Array.isArray(v),
        array: Array.isArray, null: (v) => v === null,
      };
      if (!ok[operand]!(actual)) fail(`expected a ${operand}`);
      return;
    }
    case "$startsWith": if (typeof actual !== "string" || !actual.startsWith(operand)) fail(`expected text starting with ${short(operand)}`); return;
    case "$all":
      if (!Array.isArray(actual) || actual.length === 0) fail("expected a non-empty list");
      (actual as Json[]).forEach((item, i) => match(item, operand, `${where}/${i}`));
      return;
    case "$textExcludes": if (toJson(actual).includes(operand)) fail(`expected no text ${short(operand)} anywhere`); return;
    case "$absent": if (operand) fail("expected absent"); return;
    default: throw new Error(`unknown matcher ${operator}`);
  }
}

export function match(actual: Json, expected: Json, where = ""): void {
  if (isOperatorObject(expected)) {
    for (const [operator, operand] of Object.entries(expected)) operate(actual, operator, operand, where);
    return;
  }
  if (expected !== null && typeof expected === "object" && !Array.isArray(expected)) {
    if (actual === null || typeof actual !== "object" || Array.isArray(actual)) throw new CaseFailure(`${where || "/"}: expected an object, got ${short(actual)}`);
    for (const [key, wanted] of Object.entries(expected as Record<string, Json>)) {
      if (key.startsWith("$") && key !== "$ids") { operate(actual, key, wanted, where); continue; }
      if (key === "$ids") { match(recalledIds(actual), Array.isArray(wanted) ? [...wanted].sort() : wanted, `${where}/$ids`); continue; }
      const value = key.startsWith("/") ? getPointer(actual, key) : key in actual ? actual[key] : MISSING;
      const path = key.startsWith("/") ? `${where}${key}` : `${where}/${key}`;
      if (wanted && typeof wanted === "object" && wanted.$absent) { if (value !== MISSING) throw new CaseFailure(`${path}: expected absent, got ${short(value)}`); continue; }
      if (wanted && typeof wanted === "object" && wanted.$empty) {
        const empty = value === MISSING || value === null || value === "" || (Array.isArray(value) && value.length === 0) || (typeof value === "object" && Object.keys(value).length === 0);
        if (!empty) throw new CaseFailure(`${path}: expected absent or empty, got ${short(value)}`);
        continue;
      }
      if (value === MISSING) throw new CaseFailure(`${path}: missing (expected ${short(wanted)})`);
      match(value, wanted, path);
    }
    return;
  }
  if (Array.isArray(expected)) {
    if (!Array.isArray(actual) || actual.length !== expected.length) throw new CaseFailure(`${where || "/"}: expected ${short(expected)}, got ${short(actual)}`);
    expected.forEach((e, i) => match(actual[i], e, `${where}/${i}`));
    return;
  }
  const same = typeof actual === "bigint" || typeof expected === "bigint" ? BigInt(actual) === BigInt(expected) : actual === expected;
  if (!same) throw new CaseFailure(`${where || "/"}: expected ${short(expected)}, got ${short(actual)}`);
}

// The stubs -----------------------------------------------------------------------------------------------------------
export class Stubs {
  private constructor(private readonly child: ChildProcess, readonly url: string) {}

  static async start(): Promise<Stubs> {
    const python = process.env.PYTHON ?? (process.platform === "win32" ? "python" : "python3");
    const child = spawn(python, [join(GATE, "stubs.py")], { stdio: ["pipe", "pipe", "inherit"] });
    const line = await new Promise<string>((resolve, reject) => {
      createInterface({ input: child.stdout! }).once("line", resolve);
      child.once("error", reject);
      child.once("exit", (code) => reject(new Error(`stubs.py exited (${code})`)));
    });
    return new Stubs(child, `http://127.0.0.1:${JSON.parse(line).port}`);
  }

  async control(body: Json): Promise<Json> {
    const response = await fetch(`${this.url}/_stub/control`, { method: "POST", body: JSON.stringify(body), headers: { "Content-Type": "application/json" } });
    return response.json();
  }

  async seen(): Promise<Json> {
    return (await fetch(`${this.url}/_stub/seen`)).json();
  }

  close(): void {
    this.child.stdin!.end();
    setTimeout(() => this.child.kill(), 10_000).unref();
  }
}

// One case ------------------------------------------------------------------------------------------------------------
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

class Run {
  readonly fn: NativeFunctions = load();
  readonly contexts = new Map<string, bigint>();
  readonly jobs = new Map<string, [bigint, bigint] | [bigint, bigint][]>();
  readonly vars: Record<string, Json>;

  constructor(readonly c: Case, readonly env: Environment, readonly fx: Json, readonly stubs: Stubs | null) {
    this.vars = { stateDir: env.freshState() };
  }

  resolve(value: Json): Json {
    return resolveValue(value, this.env, this.fx, this.vars);
  }

  context(args: Json): bigint {
    const name = this.resolve(args.ctx ?? "ctx");
    if (typeof name === "number" || typeof name === "bigint") return BigInt(name);
    const found = this.contexts.get(name);
    if (found === undefined) throw new Error(`no context named ${name}`);
    return found;
  }

  job(args: Json): [bigint, bigint] {
    return this.jobs.get(args.job ?? "job") as [bigint, bigint];
  }

  requestBytes(args: Json): Uint8Array {
    return "raw" in args ? rawBytes(args.raw, this.env, this.fx, this.vars) : encoder.encode(toJson(this.resolve(args.request)));
  }

  submit(args: Json): [number, bigint] {
    const data = this.requestBytes(args);
    const out: unknown[] = [0];
    const status = this.fn.am_v1_submit(this.context(args), data, data.length, Number(args.timeoutMs ?? 0), out);
    return [status, handle(out[0])];
  }

  poll(context: bigint, job: bigint, waitMs: number): [number, Json] {
    const disposition: unknown[] = [PENDING], pointer: unknown[] = [null], length: unknown[] = [0];
    const status = this.fn.am_v1_poll(context, job, waitMs, disposition, pointer, length);
    const data = takeBuffer(this.fn, pointer[0], length[0]);
    if (status !== Status.OK || disposition[0] !== TERMINAL || data === null) return [status, null];
    return [status, JSON.parse(data.toString("utf8"))];
  }

  async awaitResult(context: bigint, job: bigint, budgetS: number): Promise<Json> {
    const deadline = Date.now() + budgetS * 1000;
    for (;;) {
      const [status, result] = this.poll(context, job, 50);
      if (status !== Status.OK) throw new CaseFailure(`am_v1_poll returned ${statusName(status)} while waiting`);
      if (result !== null) return result;
      if (Date.now() > deadline) throw new CaseFailure(`no result within ${budgetS} s`);
      await sleep(0); // let the stubs' HTTP answers and timers run
    }
  }

  async close(context: bigint, waitS: number): Promise<number> {
    const deadline = Date.now() + waitS * 1000;
    for (;;) {
      const status = this.fn.am_v1_close(context);
      if (status !== Status.BUSY || Date.now() > deadline) return status;
      await sleep(10);
    }
  }

  async step(step: Json): Promise<Json> {
    const action = Object.keys(step).find((k) => !["expect", "as", "note", "save"].includes(k))!;
    let args = step[action] && typeof step[action] === "object" ? { ...step[action] } : {};
    if ("as" in step) args = { ...args, as: step.as };
    const outcome = await this.act(action, args);
    if ("save" in step) this.vars[step.save] = outcome;
    return outcome;
  }

  async act(action: string, a: Json): Promise<Json> {
    const fn = this.fn;
    switch (action) {
      case "abi": return { version: fn.am_v1_abi_version(), hasWait: hasWait(fn) };
      case "open": {
        const data = "raw" in a ? rawBytes(a.raw, this.env, this.fx, this.vars) : encoder.encode(toJson(this.resolve(a.config)));
        const context: unknown[] = [0], pointer: unknown[] = [null], length: unknown[] = [0];
        const status = fn.am_v1_open(a.abiVersion ?? ABI_VERSION, data, data.length, context, pointer, length);
        const document = takeBuffer(fn, pointer[0], length[0]);
        const outcome: Json = { status: statusName(status) };
        if (status === Status.OK) {
          this.contexts.set(a.as ?? "ctx", handle(context[0]));
          outcome.handle = handle(context[0]);
        } else if (document) {
          const error = JSON.parse(document.toString("utf8")).error ?? {};
          Object.assign(outcome, { code: error.code, message: error.message ?? "" });
        }
        return outcome;
      }
      case "close": {
        const status = await this.close(this.context(a), Number(a.waitS ?? 10));
        if (status === Status.OK && typeof (a.ctx ?? "ctx") === "string") this.contexts.delete(a.ctx ?? "ctx");
        return { status: statusName(status) };
      }
      case "submit": {
        const [status, job] = this.submit(a);
        if (status === Status.OK) this.jobs.set(a.as ?? "job", [this.context(a), job]);
        return { status: statusName(status) };
      }
      case "await": { const [c, j] = this.job(a); return this.awaitResult(c, j, Number(a.budgetS ?? 30)); }
      case "call": {
        const [status, job] = this.submit(a);
        if (status !== Status.OK) throw new CaseFailure(`am_v1_submit returned ${statusName(status)}`);
        const context = this.context(a);
        try {
          return await this.awaitResult(context, job, Number(a.budgetS ?? 30));
        } finally {
          fn.am_v1_job_release(context, job);
        }
      }
      case "poll": { const [c, j] = this.job(a); const [status, result] = this.poll(c, j, Number(a.waitMs ?? 0)); return { status: statusName(status), result }; }
      case "cancel": {
        const [c, j] = "handle" in a ? [this.context(a), BigInt(a.handle)] : this.job(a);
        return { status: statusName(fn.am_v1_cancel(c, j)) };
      }
      case "release": { const [c, j] = this.job(a); return { status: statusName(fn.am_v1_job_release(c, j)) }; }
      case "state": {
        const v: unknown[][] = [[0], [0], [0], [0]], flags: unknown[] = [0];
        const status = fn.am_v1_state(this.context(a), v[0]!, v[1]!, v[2]!, v[3]!, flags);
        const f = Number(flags[0]);
        return {
          status: statusName(status), stateRevision: handle(v[0]![0]), policyEpoch: handle(v[1]![0]), deletionEpoch: handle(v[2]![0]),
          memoryRevision: handle(v[3]![0]), readAllowed: (f & Flags.READ_ALLOWED) !== 0, admissionAllowed: (f & Flags.ADMISSION_ALLOWED) !== 0,
          writeQuiescent: (f & Flags.WRITE_QUIESCENT) !== 0, closing: (f & Flags.CLOSING) !== 0, erasePending: (f & Flags.ERASE_PENDING) !== 0,
        };
      }
      case "submitMany": {
        const jobs: [bigint, bigint][] = [];
        const refused: Record<string, number> = {};
        for (let i = 0; i < Number(a.count); i++) {
          const [status, job] = this.submit(a);
          if (status === Status.OK) jobs.push([this.context(a), job]);
          else refused[statusName(status)] = (refused[statusName(status)] ?? 0) + 1;
        }
        this.jobs.set(a.as ?? "jobs", jobs);
        return { admitted: jobs.length, refused };
      }
      case "awaitAll": {
        const results = [];
        for (const [c, j] of (this.jobs.get(a.jobs ?? "jobs") as [bigint, bigint][]) ?? []) {
          try { results.push(await this.awaitResult(c, j, Number(a.budgetS ?? 30))); } finally { fn.am_v1_job_release(c, j); }
        }
        this.jobs.set(a.jobs ?? "jobs", []);
        return { results };
      }
      case "drain": {
        const context = this.context(a);
        const reported: bigint[] = [];
        const deadline = Date.now() + Number(a.budgetS ?? 30) * 1000;
        while (reported.length < Number(a.count) && Date.now() < deadline) {
          const out: unknown[] = [0];
          const status = fn.am_v1_wait!(context, Number(a.waitMs ?? 1000), out) as number;
          if (status !== Status.OK) throw new CaseFailure(`am_v1_wait returned ${statusName(status)}`);
          if (handle(out[0]) !== 0n) reported.push(handle(out[0]));
        }
        const submitted = (((this.jobs.get(a.jobs ?? "jobs") as [bigint, bigint][]) ?? []).map(([, j]) => j)).sort();
        const results = a.takeResults ? reported.map((j) => this.poll(context, j, 0)[1]) : [];
        return { reported: reported.length, sameAsSubmitted: toJson([...reported].sort().map(String)) === toJson(submitted.map(String)), results };
      }
      case "waitAny": {
        const out: unknown[] = [0];
        const status = fn.am_v1_wait!(this.context(a), Number(a.waitMs ?? 0), out) as number;
        return { status: statusName(status), job: Number(handle(out[0])) };
      }
      case "releaseAll": {
        for (const [c, j] of (this.jobs.get(a.jobs ?? "jobs") as [bigint, bigint][]) ?? []) fn.am_v1_job_release(c, j);
        this.jobs.set(a.jobs ?? "jobs", []);
        return {};
      }
      case "repeat": {
        let outcome: Json = null;
        for (let attempt = 0; attempt < Number(a.times); attempt++) {
          if (attempt && a.sleepS) await sleep(Number(a.sleepS) * 1000);
          outcome = await this.step(a.step);
          if ("until" in a && matches(outcome, this.resolve(a.until))) break;
        }
        return outcome;
      }
      case "stub": {
        if (!this.stubs) throw new Skip("no stubs in this run");
        return a.seen ? this.stubs.seen() : this.stubs.control(a);
      }
      case "set": {
        for (const [name, value] of Object.entries(a as Record<string, Json>)) {
          this.vars[name] = value && typeof value === "object" && "$unique" in value
            ? `${value.$unique}${[...crypto.getRandomValues(new Uint8Array(8))].map((b) => b.toString(16).padStart(2, "0")).join("")}`
            : this.resolve(value);
        }
        return {};
      }
      case "sleep": await sleep(Number(a.seconds ?? 0) * 1000); return {};
      default: throw new Error(`unknown step ${action}`);
    }
  }

  async finish(): Promise<void> {
    if (this.stubs && this.c.stubs) await this.stubs.control({ mode: "ok", release: true });
    for (const entry of this.jobs.values()) {
      // A list of [context, job] pairs (submitMany), or one pair (submit); an emptied list holds nothing.
      const pairs = (entry.length > 0 && typeof entry[0] === "bigint" ? [entry] : entry) as [bigint, bigint][];
      for (const [c, j] of pairs) this.fn.am_v1_job_release(c, j);
    }
    for (const context of this.contexts.values()) await this.close(context, 30);
  }
}

/** Runs one case; throws Skip, or CaseFailure naming the step that failed. */
export async function runCase(c: Case, env: Environment, fx: Json, stubs: Stubs | null): Promise<void> {
  for (const need of c.needs ?? []) if (env.vars[need] === null || env.vars[need] === undefined) throw new Skip(`needs ${need}`);
  const run = new Run(c, env, fx, stubs);
  try {
    if (stubs && c.stubs) await stubs.control({ reset: true });
    for (const [index, step] of c.steps.entries()) {
      const outcome = await run.step(step);
      if ("expect" in step) {
        try {
          match(outcome, run.resolve(step.expect));
        } catch (failure) {
          if (!(failure instanceof CaseFailure)) throw failure;
          const action = Object.keys(step).find((k) => !["expect", "as", "note", "save"].includes(k));
          throw new CaseFailure(`${c.id}, step ${index + 1} (${action}): ${failure.message}`);
        }
      }
    }
  } finally {
    await run.finish();
  }
}

export async function main(argv: string[]): Promise<number> {
  const at = argv.indexOf("--tier");
  const tier = (at >= 0 ? argv[at + 1]! : process.env.GATE_TIER ?? "A").toUpperCase();
  const only = argv.includes("--case") ? argv[argv.indexOf("--case") + 1] : undefined;
  const fx = fixtures();
  const stubs = await Stubs.start();
  const env = new Environment(tier, stubs.url);
  let passed = 0, failed = 0, skipped = 0;
  try {
    for (const c of loadCases(tier).filter((c) => !only || c.id === only)) {
      const started = performance.now();
      try {
        await runCase(c, env, fx, stubs);
        passed++;
        console.log(`ok      ${c.id} (${Math.round(performance.now() - started)} ms)`);
      } catch (error) {
        if (error instanceof Skip) { skipped++; console.log(`skipped ${c.id}: ${error.message}`); }
        else { failed++; console.log(`FAIL    ${c.id}: ${(error as Error).message}`); }
      }
    }
  } finally {
    stubs.close();
  }
  console.log(`tier ${tier}: ${passed} passed, ${failed} failed, ${skipped} skipped (TypeScript, Node ${process.versions.node})`);
  return failed ? 1 : 0;
}

if (import.meta.url === `file://${process.argv[1]?.replace(/\\/g, "/").replace(/^([A-Za-z]):/, "/$1:")}` || process.argv[1]?.endsWith("runner.js")) {
  main(process.argv.slice(2)).then((code) => process.exit(code));
}
