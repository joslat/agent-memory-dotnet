# ampack for Python

Verify and read **ampack** memory packs: knowledge, ontologies and minds for AI agents, in one signed file that is
checked where it lands.

This is a preview (0.1.0). It verifies and reads. It does not build, sign or certify a pack.

```
pip install ampack
```

Needs Python 3.12 and the `cryptography` package.

## Verify a pack

A pack is accepted only when a key in **your** trust policy signed it as publisher for its id.

```
ampack verify notes.ampack trust.json
```

Exit code 0 means verified, 1 refused (with the reason on the line), 2 a usage error or a defect.

## Read a pack

```python
import ampack

try:
    pack = ampack.load("notes.ampack", trust="trust.json")
except ampack.Refused as reason:
    raise SystemExit(f"not loaded: {reason}")

print(pack.id, pack.version, pack.digest, pack.kind)
print(pack.card["intendedUse"] if pack.card else "no card")

for record in pack.records():
    print(record.type, record.key, dict(record.properties))

for member in pack.members:          # a pack set carries several packs
    print(member.id, member.kind)
```

Nothing is returned from a pack that did not verify. `pack`, `trust` and each revocation document may be a path or the
bytes themselves. `ampack.verify(...)` gives the verdict alone.

## What it checks, and what it does not

- **Checked, as the specification says:** the strict archive, every file's length and SHA-256 against the signed list,
  the signatures against the trust policy (key scopes, roles, validity windows, revocations), `pack.json`, the card,
  composites and their locks, pack sets and each member, embeddings, the usage policy's read door.
- **Checked on each record line:** its shape only. An object with a string type and key, properties as an object, links
  as `{"relation", "target"}` pairs, no field the format does not define, and a knowledge pack's limits (5,000 records,
  16 files, 64 KiB a line).
- **Not checked by this reader:** whether a record fits the pack's ontology (its type, required properties, relations),
  and how deeply a record nests. A host that imports records checks those.

The verifier is the Python verifier of the format's conformance kit, unchanged. It shares no code with the .NET and
Rust verifiers, and must reach the same verdict as they do on every conformance vector.

## Why Python 3.12 only

The format decides whether text is normalized with Unicode 15.0 data. Python 3.12 carries exactly that version; other
versions carry another, and could give another verdict on the same pack. A later release will remove this limit.

## Links

- The format, the hub and certified packs: https://memcert.dev
- Specification and test vectors: https://github.com/joslat/agent-memory-dotnet/tree/main/spec
- Paper: https://doi.org/10.5281/zenodo.24012989

Apache-2.0. Patent pending (Swiss application CH001531/2026); see `PATENTS.md` in the repository for the proposed
pledge.
