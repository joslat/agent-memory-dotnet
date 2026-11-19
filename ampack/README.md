# ampack: memory packs your agents can load and trust

An **ampack** file is a memory pack: knowledge, an ontology or a mind for an AI agent, in one signed file that is
checked where it lands. The format is open and does not depend on any agent framework or language.

This folder is the front door for the **client packages**: the code that verifies and reads a pack, in each language.

> Clients verify, inspect and read. They do not build, sign or certify a pack.

## Install

| Language | Package | Install | State |
|---|---|---|---|
| Python | [`ampack`](python/) | `pip install ampack` | Preview 0.1.0, Python 3.12 |
| JavaScript and TypeScript | `ampack` | `npm install ampack` | Being prepared, from [`reader-rs`](../reader-rs/) |
| Rust | `ampack-core`, `ampack` | `cargo add ampack-core` | Being prepared, from [`reader-rs`](../reader-rs/) |
| .NET | `Ampack` | | Planned. Today the verifier is [`src/AgentMemory.Packs.Format`](../src/AgentMemory.Packs.Format/) |

A row says "being prepared" or "planned" until its package is on its index.

## Thirty seconds, in Python

```python
import ampack

pack = ampack.load("notes.ampack", trust="trust.json")   # raises ampack.Refused with the reason
for record in pack.records():
    print(record.type, record.key, dict(record.properties))
```

A pack loads only when a key in **your** trust policy (`trust.json`) signed it as publisher for its id. That is the whole
idea: the file carries its proof, and the host decides whom it trusts.

## Where everything is

| What | Where |
|---|---|
| The specification | [`spec/`](../spec/) (start with [`spec/core.md`](../spec/core.md)) |
| The conformance vectors and the independent Python verifier | [`conformance/pack-v1.1/`](../conformance/pack-v1.1/) |
| The reader in Rust and WebAssembly | [`reader-rs/`](../reader-rs/) |
| The .NET verifier and the `ampack` tool | [`src/AgentMemory.Packs.Format/`](../src/AgentMemory.Packs.Format/), [`tools/Ampack/`](../tools/Ampack/) |
| Certified packs and the hub | https://memcert.dev |
| The paper | https://doi.org/10.5281/zenodo.24012989 |

Three verifiers that share no code (.NET, Python, Rust) must reach the same verdict on every conformance vector. A
client package ships one of them unchanged.

## Licence

Apache-2.0 (`LICENSE` and `NOTICE` here); the specification is CC BY 4.0. Patent pending (Swiss application
CH001531/2026); the proposed pledge for implementations is [`PATENTS.md`](../PATENTS.md). The full map is
[`LICENSING.md`](../LICENSING.md).
