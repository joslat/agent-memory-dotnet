// SPDX-License-Identifier: Apache-2.0
// The end-to-end driver for the npm package: loads one pack as a user of the package does and prints what it read.
//
// usage: node driver.mjs <pack> <trust.json> [--revocations <file>]... [--at <RFC 3339>]
//
// Prints one line of JSON: {"verified": true, "pack": {...}} or {"verified": false, "reason": "..."}. Exit code 0 in
// both cases; anything else is a defect of the package or of this driver.
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { load, Refused } from "ampack";

function shape(pack) {
  const files = {};
  for (const path of pack.paths) files[path] = createHash("sha256").update(pack.bytes(path)).digest("hex");
  return {
    id: pack.id,
    version: pack.version,
    digest: pack.digest ?? null,
    kind: pack.kind,
    title: pack.title ?? null,
    files,
    records: pack.records().map((r) => ({
      type: r.type, key: r.key, properties: r.properties,
      links: r.links.map((link) => [link.relation, link.target]),
      extra: r.extra, file: r.file, line: r.line,
    })),
    members: pack.members.map(shape),
  };
}

const positional = [];
const options = { revocations: [] };
const args = process.argv.slice(2);
for (let i = 0; i < args.length; i++) {
  if (args[i] === "--revocations") options.revocations.push(new Uint8Array(readFileSync(args[++i])));
  else if (args[i] === "--at") options.at = new Date(args[++i]);
  else positional.push(args[i]);
}
if (positional.length !== 2) {
  console.log("usage: node driver.mjs <pack> <trust.json> [--revocations <file>]... [--at <RFC 3339>]");
  process.exit(2);
}

let answer;
try {
  const pack = load(new Uint8Array(readFileSync(positional[0])), new Uint8Array(readFileSync(positional[1])), options);
  answer = { verified: true, pack: shape(pack) };
} catch (error) {
  if (!(error instanceof Refused)) throw error;
  answer = { verified: false, reason: error.reason };
}
process.stdout.write(JSON.stringify(answer) + "\n");
