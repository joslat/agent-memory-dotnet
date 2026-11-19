// SPDX-License-Identifier: Apache-2.0
// The package's own surface (index.js) against the conformance kit: every plain pack vector through verify(), with its
// revocation documents; and reading a verified pack. Run with `npm test` after `npm run build`.
import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { Refused, _record, inspect, load, verify } from "./index.js";

const here = dirname(fileURLToPath(import.meta.url));
const kit = join(here, "..", "..", "conformance", "pack-v1.1");
const results = JSON.parse(readFileSync(join(kit, "results.json"), "utf8"));
const vector = name => readFileSync(join(kit, "vectors", `${name}.ampack`));
const trust = readFileSync(join(kit, "trust.json"));
// One vector's policy holds a private key and is deliberately not in the repository: it cannot be run here, and a missing
// file must not count as the refusal it expects.
const plain = results.vectors.filter(v => v.kind === "pack" && !("location" in v) && !("document" in v) && existsSync(join(kit, v.policy)));

test("every plain pack vector reaches its recorded verdict, revocations included", () => {
  assert.ok(plain.length > 300, "most vectors are plain packs");
  console.log(`${plain.length} plain pack vectors of ${results.vectors.length}`);
  const wrong = [];
  for (const v of plain) {
    const revocations = (v.revocations ?? []).map(name => readFileSync(join(kit, name)));
    let verdict;
    try {
      verdict = verify(vector(v.vector), readFileSync(join(kit, v.policy)), { revocations }).verified ? "verified" : "refused";
    } catch (error) {
      verdict = `defect: ${error}`;
    }
    if (verdict !== v.expected) wrong.push(`${v.vector}: expected ${v.expected}, got ${verdict}`);
  }
  assert.deepEqual(wrong, []);
});

test("a knowledge pack reads", () => {
  const pack = load(vector("valid-python-built"), trust);
  assert.deepEqual([pack.id, pack.version, pack.kind], ["io.example.notes", "1.0.0", "knowledge"]);
  assert.match(pack.digest, /^sha256:[0-9a-f]{64}$/);
  const verdict = verify(vector("valid-python-built"), trust);
  assert.equal(verdict.verified, true);
  assert.equal(verdict.digest, pack.digest);
  const records = pack.records();
  assert.ok(records.length > 0);
  assert.ok(records.every(r => typeof r.type === "string" && typeof r.key === "string"));
  assert.equal(records[0].line, 1);
  assert.ok(pack.recordFiles.includes(records[0].file));
  assert.equal(pack.manifest.id, pack.id);
});

test("the files are the ones the digest covers", () => {
  // This vector verifies and carries a certificate nobody checked: it must not come out as a file of the verified pack.
  const pack = load(vector("attestation-certificate-content-unchecked"), trust);
  assert.ok(pack.has("pack.json"));
  assert.deepEqual(pack.paths.filter(p => p.startsWith("META/")), []);
  for (const v of plain.filter(v => v.expected === "verified" && v.policy === "trust.json" && !(v.revocations ?? []).length)) {
    const loaded = load(vector(v.vector), trust);
    for (const each of [loaded, ...loaded.members])
      assert.deepEqual(each.paths.filter(p => p === "META/files.json" || p.startsWith("META/signatures/") || p.startsWith("META/attestations/")), [], v.vector);
  }
});

test("the trust policy may be given as text", () => {
  assert.equal(load(vector("valid-python-built"), readFileSync(join(kit, "trust.json"), "utf8")).id, "io.example.notes");
});

test("a set gives its members", () => {
  const sets = plain.filter(v => v.expected === "verified" && v.policy === "trust.json" && !(v.revocations ?? []).length)
    .map(v => load(vector(v.vector), trust)).filter(p => p.kind === "set");
  assert.ok(sets.length > 0, "the kit has a verified set");
  const pack = sets[0];
  assert.deepEqual(pack.records(), []);
  assert.deepEqual(pack.members.map(m => m.id).sort(), [...pack.manifest.contains].sort());
  assert.ok(pack.members.every(m => m.kind !== "set"));
});

test("nothing comes out of a refused pack", () => {
  assert.throws(() => load(vector("tampered-record"), trust), Refused);
  const verdict = verify(vector("tampered-record"), trust);
  assert.equal(verdict.verified, false);
  assert.match(verdict.reason, /does not match META\/files\.json/);
});

test("what was verified cannot be changed through the pack", () => {
  const pack = load(vector("valid-python-built"), trust);
  const copy = pack.bytes("pack.json");
  copy.fill(0);
  assert.equal(pack.json("pack.json").id, "io.example.notes");
  assert.throws(() => { pack.id = "other"; }, TypeError);
});

test("inspect describes an archive and judges nothing", () => {
  const summary = inspect(vector("tampered-record"));
  assert.equal(summary.id, "io.example.notes");
  assert.equal(typeof summary.bytes, "number");
  assert.throws(() => inspect(new Uint8Array([1, 2, 3])), Refused);
});

test("a malformed line is refused", () => {
  const cases = [
    ['[1]', /a record is a JSON object/],
    ['{"type":"T"}', /needs a string type and key/],
    ['{"type":"T","key":1}', /needs a string type and key/],
    ['{"type":"T","key":"k","properties":[]}', /properties is an object/],
    ['{"type":"T","key":"k","links":[{"relation":"r"}]}', /links is a list/],
    ['{"type":"T","key":"k","links":[{"relation":"r","target":"t","more":1}]}', /links is a list/],
    ['{"type":"T","key":"k","colour":"red"}', /unknown field colour/],
  ];
  for (const [line, problem] of cases)
    assert.throws(() => _record(JSON.parse(line), "records/x.jsonl", 3, "records/x.jsonl:3"), problem, line);
  const kept = _record(JSON.parse('{"type":"T","key":"k","validFrom":"2026-01-01T00:00:00Z","confidenceMicro":900000}'), "records/x.jsonl", 1, "records/x.jsonl:1");
  assert.deepEqual(kept.extra, { validFrom: "2026-01-01T00:00:00Z", confidenceMicro: 900000 });
  assert.deepEqual([kept.properties, kept.links], [{}, []]);
});

test("the archive is bytes, never a path", () => {
  assert.throws(() => verify("notes.ampack", trust), TypeError);
});
