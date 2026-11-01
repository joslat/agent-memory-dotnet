// The component, through its jco JavaScript binding, on every pack vector of the kit that needs no revocation document
// (the component's verify takes an archive, a policy and a time): verdicts against results.json, and the time each takes.
import { readFileSync, existsSync } from "node:fs";
import { gzipSync } from "node:zlib";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { verifier } from "../dist/js/ampack-reader.js";

const kit = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "conformance", "pack-v1.1");
const results = JSON.parse(readFileSync(join(kit, "results.json"), "utf8"));
const now = new Date().toISOString();
let agree = 0, total = 0, skipped = 0, ms = 0, worst = 0;
const failures = [];
for (const v of results.vectors) {
  if (v.kind !== "pack" || (v.revocations || []).length || !existsSync(join(kit, v.policy))) { skipped++; continue; }
  const archive = readFileSync(join(kit, "vectors", `${v.vector}.ampack`));
  const policy = readFileSync(join(kit, v.policy));
  const t0 = performance.now();
  const verdict = verifier.verify(archive, policy, now);
  const dt = performance.now() - t0;
  ms += dt; worst = Math.max(worst, dt);
  const got = verdict.tag === "verified" ? "verified" : "refused";
  total++;
  if (got === v.expected) agree++; else failures.push(`${v.vector}: expected ${v.expected}, component ${got} (${verdict.val?.toString?.() ?? ""})`);
}
const sample = readFileSync(join(kit, "vectors", "valid-python-built.ampack"));
const t0 = performance.now();
const reps = 200;
for (let i = 0; i < reps; i++) verifier.verify(sample, readFileSync(join(kit, "trust.json")), now);
const each = (performance.now() - t0) / reps;
const summary = verifier.inspect(sample);
console.log(`component (jco): ${agree} / ${total} pack vectors agree; ${skipped} skipped (revocations, receipts, certificates, erasures, missing policy)`);
console.log(`time: ${ms.toFixed(0)} ms for all, ${(ms / total).toFixed(2)} ms mean, ${worst.toFixed(0)} ms worst; valid-python-built ${each.toFixed(2)} ms a verify`);
console.log(`inspect valid-python-built: ${JSON.stringify(summary, (k, x) => typeof x === "bigint" ? Number(x) : x)}`);
// The budget this reader states for the constrained class (R-CORE-007 leaves the numbers to each reader; feature 18):
// the component at most 640 KiB, 256 KiB gzipped, and a typical pack verified in at most 50 ms on average.
const BUDGET = { bytes: 640 * 1024, gzipped: 256 * 1024, msPerVerify: 50 };
const wasm = readFileSync(join(dirname(fileURLToPath(import.meta.url)), "..", "dist", "ampack-reader.wasm"));
const gz = gzipSync(wasm, { level: 9 }).length;
console.log(`size: ${(wasm.length / 1024).toFixed(0)} KiB, ${(gz / 1024).toFixed(0)} KiB gzipped; budget ${BUDGET.bytes / 1024} KiB, ${BUDGET.gzipped / 1024} KiB, ${BUDGET.msPerVerify} ms a verify`);
if (wasm.length > BUDGET.bytes) failures.push(`the component is ${wasm.length} bytes, over its budget of ${BUDGET.bytes}`);
if (gz > BUDGET.gzipped) failures.push(`the component is ${gz} bytes gzipped, over its budget of ${BUDGET.gzipped}`);
if (each > BUDGET.msPerVerify) failures.push(`a typical verify takes ${each.toFixed(1)} ms, over its budget of ${BUDGET.msPerVerify}`);
for (const f of failures) console.log("FAIL " + f);
process.exit(failures.length ? 1 : 0);
