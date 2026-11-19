// SPDX-License-Identifier: Apache-2.0
// ampack for JavaScript and TypeScript: verify, inspect and read a memory pack.
// The verifier is the Rust reader as a WebAssembly component (./dist). It imports nothing: no file system, no network,
// no clock. You hand it bytes. This file adds reading on top: the manifest, the card, the ontology, the record lines and
// a pack set's members. It verifies and reads; it does not build, sign or certify a pack.
import { verifier } from "./dist/ampack-reader.js";
import { RECORD_FIELDS } from "./dist/record-fields.js";

/** A pack (or a document beside it) is not accepted. `reason` says why. */
export class Refused extends Error {
  constructor(reason) {
    super(reason);
    this.name = "Refused";
    this.reason = reason;
  }
}

// A knowledge pack as a host imports it (specification, R-CORE-102).
const MAX_RECORDS = 5000, MAX_RECORD_FILES = 16, MAX_LINE_BYTES = 64 * 1024;
const utf8 = new TextDecoder("utf-8", { fatal: true });

function bytes(value, what) {
  if (value instanceof Uint8Array) return value;
  if (value instanceof ArrayBuffer) return new Uint8Array(value);
  if (typeof value === "string" && what !== "the archive") return new TextEncoder().encode(value);
  throw new TypeError(`${what} is given as bytes (a Uint8Array or an ArrayBuffer)` + (what === "the archive" ? "" : " or as its JSON text"));
}

function instant(at) {
  if (at === undefined) return new Date().toISOString();
  if (at instanceof Date && !Number.isNaN(at.getTime())) return at.toISOString();
  throw new TypeError("'at' is a Date");
}

// The component throws what its interface calls an error (a text); anything else is a defect and is passed on as it is.
function reason(error) {
  if (typeof error === "string") return error;
  if (error && typeof error.payload === "string") return error.payload;
  throw error;
}

/**
 * Verifies a pack (or a pack set and each member) against a trust policy.
 * A pack is accepted only when a key in the policy signed it as publisher for its id.
 */
export function verify(archive, trust, options = {}) {
  const { revocations = [], at } = options;
  if (revocations.length > 0) {
    try {
      const { info } = verifier.load(bytes(archive, "the archive"), bytes(trust, "the trust policy"),
        revocations.map(r => bytes(r, "a revocation document")), instant(at));
      return { verified: true, ...info };
    } catch (error) {
      return { verified: false, reason: reason(error) };
    }
  }
  const verdict = verifier.verify(bytes(archive, "the archive"), bytes(trust, "the trust policy"), instant(at));
  return verdict.tag === "verified" ? { verified: true, ...verdict.val } : { verified: false, reason: verdict.val };
}

/** What an archive says about itself. No signature is judged: never treat this as a verdict. */
export function inspect(archive) {
  try {
    const summary = verifier.inspect(bytes(archive, "the archive"));
    return { ...summary, bytes: Number(summary.bytes) };
  } catch (error) {
    throw new Refused(reason(error));
  }
}

/** Verifies a pack as verify() does and returns it for reading. Throws Refused; nothing comes out of a refused pack. */
export function load(archive, trust, options = {}) {
  const { revocations = [], at } = options;
  let loaded;
  try {
    loaded = verifier.load(bytes(archive, "the archive"), bytes(trust, "the trust policy"),
      revocations.map(r => bytes(r, "a revocation document")), instant(at));
  } catch (error) {
    throw new Refused(reason(error));
  }
  return new Pack(new Map(loaded.files.map(f => [f.path, f.data])), loaded.info);
}

/** A verified pack. Create one with load(). */
export class Pack {
  #files;
  constructor(files, info) {
    this.#files = files;
    this.id = info.id;
    this.version = info.version;
    this.digest = info.digest;
    this.kind = info.kind;
    this.profiles = info.profiles;
    Object.freeze(this);
  }

  /** The paths of every file of the pack, as verified. */
  get paths() { return [...this.#files.keys()]; }
  has(path) { return this.#files.has(path); }

  /** A file's bytes (a copy); undefined when the pack has no such file. */
  bytes(path) {
    const data = this.#files.get(path);
    return data === undefined ? undefined : data.slice();
  }
  text(path) {
    const data = this.#files.get(path);
    return data === undefined ? undefined : utf8.decode(data);
  }
  json(path) {
    const text = this.text(path);
    return text === undefined ? undefined : JSON.parse(text);
  }

  get manifest() { return this.json("pack.json"); }
  get title() { return this.manifest?.display?.title; }
  /** The pack card (owner, intended use, limitations), or undefined. */
  get card() { return this.json("card.json"); }
  /** The types and relations the pack declares, or undefined. */
  get ontology() { return this.json("ontology.json"); }
  get recordFiles() { return this.paths.filter(p => p.startsWith("records/") && p.endsWith(".jsonl")).sort(); }

  /**
   * The pack's own record lines, file by file in name order. A set or a composite has none: see members.
   * Each line is checked for its shape only. Whether a record fits the ontology is for the importing host to check.
   */
  records() {
    const paths = this.recordFiles, knowledge = this.kind === "knowledge", out = [];
    if (knowledge && paths.length > MAX_RECORD_FILES)
      throw new Refused(`a knowledge pack holds at most ${MAX_RECORD_FILES} record files; this one has ${paths.length}`);
    for (const path of paths) {
      const data = this.#files.get(path);
      let start = 0, line = 0;
      while (start < data.length) {
        let stop = data.indexOf(10, start);
        if (stop < 0) stop = data.length;
        line++;
        const at = `${path}:${line}`;
        if (knowledge && stop - start > MAX_LINE_BYTES) throw new Refused(`${at}: longer than ${MAX_LINE_BYTES} bytes`);
        out.push(record(JSON.parse(utf8.decode(data.subarray(start, stop))), path, line, at));
        if (knowledge && out.length > MAX_RECORDS) throw new Refused(`a knowledge pack holds at most ${MAX_RECORDS} records`);
        start = stop + 1;
      }
    }
    return out;
  }

  /** For a pack set: its member packs, each already verified on its own. Their digest is not given in this preview. */
  get members() {
    if (this.kind !== "set") return [];
    const out = [];
    for (const id of [...(this.manifest.contains ?? [])].sort()) {
      const prefix = `packs/${id}/`;
      const inner = new Map();
      for (const [path, data] of this.#files) if (path.startsWith(prefix)) inner.set(path.slice(prefix.length), data);
      const manifest = JSON.parse(utf8.decode(inner.get("pack.json")));
      out.push(new Pack(inner, { id: manifest.id, version: manifest.version, digest: undefined, kind: manifest.kind, profiles: [] }));
    }
    return out;
  }
}

const isObject = v => v !== null && typeof v === "object" && !Array.isArray(v);

function record(value, file, line, at) {
  if (!isObject(value)) throw new Refused(`${at}: a record is a JSON object`);
  const unknown = Object.keys(value).filter(k => !RECORD_FIELDS.has(k)).sort();
  if (unknown.length > 0) throw new Refused(`${at}: unknown field ${unknown.join(", ")}`);
  if (typeof value.type !== "string" || typeof value.key !== "string") throw new Refused(`${at}: a record needs a string type and key`);
  const properties = value.properties ?? {};
  if (!isObject(properties)) throw new Refused(`${at}: properties is an object`);
  const links = value.links ?? [];
  const pair = l => isObject(l) && Object.keys(l).length === 2 && typeof l.relation === "string" && typeof l.target === "string";
  if (!Array.isArray(links) || !links.every(pair)) throw new Refused(`${at}: links is a list of {"relation", "target"} with string values`);
  const { type, key, properties: _properties, links: _links, ...extra } = value;
  return { type, key, properties, links, extra, file, line };
}

// For the tests: the line check on its own.
export const _record = record;
