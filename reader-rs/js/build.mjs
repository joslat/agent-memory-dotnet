// SPDX-License-Identifier: Apache-2.0
// Completes the package after `npm run transpile`: the licence files, and the names a record line may carry, each taken
// from its one place in the repository. What this writes is a build product: never edited, not committed.
import { copyFileSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
for (const name of ["LICENSE", "NOTICE"]) copyFileSync(join(here, "..", name), join(here, name));

const registry = JSON.parse(readFileSync(join(here, "..", "..", "spec", "registry.json"), "utf8"));
const fields = ["type", "key", "properties", "links", ...registry.records.lifeFields, ...registry.records.trustFields];
writeFileSync(join(here, "dist", "record-fields.js"),
  "// Made by build.mjs from spec/registry.json (records.lifeFields and records.trustFields). Do not edit.\n" +
  `export const RECORD_FIELDS = new Set(${JSON.stringify(fields)});\n`);
console.log(`record fields: ${fields.join(", ")}`);
