// The component, through its jco JavaScript binding, on every pack vector of the kit that needs no revocation document
// (the component's verify takes an archive, a policy and a time): verdicts against results.json, and the time each takes.
import { readFileSync, existsSync } from "node:fs";
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
for (const f of failures) console.log("FAIL " + f);
process.exit(failures.length ? 1 : 0);
