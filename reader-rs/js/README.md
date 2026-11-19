# ampack for JavaScript and TypeScript

Verify and read **ampack** memory packs: knowledge, ontologies and minds for AI agents, in one signed file that is
checked where it lands.

This is a preview (0.1.0). It verifies, inspects and reads. It does not build, sign or certify a pack.

```
npm install ampack
```

The verifier is the Rust reader compiled to WebAssembly (0.5 MB). It imports nothing: no file system, no network, no
clock. You hand it bytes, so the same package runs in Node 20 or later and in a browser. TypeScript types are included.

## Verify a pack

A pack is accepted only when a key in **your** trust policy signed it as publisher for its id.

```js
import { readFileSync } from "node:fs";
import { verify } from "ampack";

const verdict = verify(readFileSync("notes.ampack"), readFileSync("trust.json"));
if (verdict.verified) console.log(verdict.id, verdict.version, verdict.digest);
else console.log("refused:", verdict.reason);
```

## Read a pack

```js
import { load, Refused } from "ampack";

try {
  const pack = load(archiveBytes, trustBytes, { revocations: [revocationBytes] });
  console.log(pack.id, pack.kind, pack.card?.intendedUse);
  for (const record of pack.records()) console.log(record.type, record.key, record.properties);
  for (const member of pack.members) console.log(member.id, member.kind);   // a pack set carries several packs
} catch (error) {
  if (error instanceof Refused) console.log("not loaded:", error.reason);
  else throw error;
}
```

Nothing comes out of a pack that did not verify. `inspect(archiveBytes)` describes an archive without judging any
signature: never treat it as a verdict.

## What it checks, and what it does not

- **Checked, as the specification says:** the strict archive, every file's length and SHA-256 against the signed list,
  the signatures against the trust policy (key scopes, roles, validity windows, revocations), `pack.json`, the card,
  composites and their locks, pack sets and each member, embeddings, the usage policy's read door.
- **Checked on each record line:** its shape only. An object with a string type and key, properties as an object, links
  as `{"relation", "target"}` pairs, no field the format does not define, and a knowledge pack's limits (5,000 records,
  16 files, 64 KiB a line).
- **Not checked by this reader:** whether a record fits the pack's ontology (its type, required properties, relations),
  and how deeply a record nests. A host that imports records checks those.
- **Not in this preview:** publication receipts, certificates and erasure statements (the Rust command line checks them),
  and the digest of a set's member.

The Rust reader shares no code with the .NET and Python verifiers, and must reach the same verdict as they do on every
conformance vector.

## Links

- The format, the hub and certified packs: https://memcert.dev
- Specification and test vectors: https://github.com/joslat/agent-memory-dotnet/tree/main/spec
- Paper: https://doi.org/10.5281/zenodo.24012989

Apache-2.0. Patent pending (Swiss application CH001531/2026); see `PATENTS.md` in the repository for the proposed
pledge.
