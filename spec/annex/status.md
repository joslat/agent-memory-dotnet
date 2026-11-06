# Annex: status (informative)

This annex says what the reference implementation in this repository builds today. It is informative: nothing here is a
requirement, and it changes as the implementation does. Status as of 1 November 2026.

## Readers and the conformance kit

- Three verifiers implement Core and the profiles marked implemented in `registry.json`: a .NET verifier
  (`ampack verify`), a Python verifier (`conformance/pack-v1.1/python/ampack_verify.py`, standard library plus one
  cryptography package), and a Rust reader (`reader-rs/`, `ampack-rs`, also built as a WebAssembly component), written
  later against the same kit; `conformance/pack-v1.1/run_third.py` runs it on every vector.
- The conformance kit (`conformance/pack-v1.1/run_vectors.py`) builds 512 named vectors, each expected to verify or to be
  refused, and requires both verifiers to reach the expected outcome, each refused vector for its own rule (its reason is
  pinned). On every verified vector both verifiers must also derive the same profiles from the registry's map.
- A differential fuzzer mutates archives, deflated bytes, single JSON and JSONL files, `validUntil` forms, edge paths, the
  signature envelope and the trust policy, and requires every verifier to agree (the Rust reader too, when built). Revocation lists, receipts, certificates,
  later minors and timestamp tokens are held by named vectors, not mutated.
- The trial statistic is checked exactly over every outcome (`eng/trial-statistics/coverage.py`) and by a seeded harness
  over named, random, rare-loss, near-equality and burst regimes.
- Every vector names the requirements it tests (`vectors.json`); `SpecRequirementsTests` holds the spec text, the
  requirement list and the map equal.

## Profiles

| Profile | State |
|---|---|
| Core | Built in both verifiers and in the module host |
| Walk | Built in the module host, over two stores (a graph database and an in-memory store) that a cross-store test walks alike |
| Trial | Built: the gate, the `host` baseline, confidence bounds, precision clustered by task, mandatory namespaces, floors, upgrades |
| Compose | Built: composite locks, pack sets, nesting four deep, consent for walks and links, host grants |
| Certify | Built: certificates, the AEF run beside them, certificate trust per namespace |
| Federation | Built in both verifiers, with publication receipts and a Solid adapter on WAC and ACP |
| Embeddings | Built in both verifiers and the Rust reader; a host uses vectors only on an exact model match |
| Permissions | Built: the usage policy in all three verifiers; read at verify, export and rehydrate at serve, rehydrate in the module set; every record names its policy |
| Protection, Code | Declared only; refused at load until enforced |

Beyond the profiles, Core carries the signed-content documents (the card, the derivation, the usage policy) and the
statements beside a pack (certificates, publication receipts, erasures, trial aggregates), each read alike by every
verifier. Hosts also hold record lives and lifecycles, confidence capped by source and signer, decisions that expire,
shadow trials, and retrieval trials on a host task file.

## Tools

The `ampack` command builds, signs, verifies, inspects and lints packs, and also: keygen, init (knowledge, definition),
lock, migrate, set, test, gate, admit, certify, certificate verify, receipt verify, ledger, erase, erasure verify and
receipt, aggregate create and verify, import (`json` through a mapping file, `text`, and `folder` for a Markdown memory
folder, each writing a signed knowledge pack of notes and reporting what it left out), and serve (with `--print-config` for
MCP clients). `agentmemory trial` runs the code
map's trial on a C# repository in memory, with no database. Every command is
tested as a process.

`ampack serve` mounts verified packs in an in-process engine and serves them over MCP (standard input and output) and a
REST surface (`GET /v1/meta`, `GET /v1/packs`, `POST /v1/packs/{id}/views/{view}/ask`, `POST /v1/walk`). Beyond the local
machine, every request needs a bearer token of at least 32 characters, packs published for named WebIDs are not served,
at most 16 requests are answered at once, and TLS comes from a reverse proxy. Without `--allow-remote`, a request from
elsewhere, or one carrying `Forwarded`, `Via`, `X-Forwarded-*` or `X-Real-IP`, is refused. It takes the host's consent
grants and gate options (the floor among them) through the same code a host uses. Over MCP it also offers resources
(`ampack://status/{id}`, and per pack digest its card, a view and a record) and a read-only `memory_view` folder, through the
same door as its tools: a pack that is off answers with its reason, never with records.

## Known differences and limits

- `ampack serve` drops a record carried by a snapshot pack that expired from the offers after the walk, so such a record
  may still have steered the walk.
- Between stores: a link into another module's records is resolved when the linking record is written, so in the graph
  store a link to a record that arrives later is missing until the linking module is imported again, while the in-memory
  store resolves links when it mounts. Import a pack's dependencies first.
- An activation record deleted from the host's folder can re-enable an older passing decision (host-owned state).
- Records imported from a snapshot stay after its carrier expires until re-imported or removed.
- A code point assigned after Unicode 15.0 may read differently on a newer host.
- Only a version of the same form is an upgrade's predecessor: a pack that replaces compiled code of the same id starts
  a new chain, since the runner measures pack files only.
- An evaluator key's expiry is checked when the host loads the pack, not while it runs.
- The serving binary is self-contained single-file, not ahead-of-time compiled.
- `ampack serve` judges the trial gate once, at startup: a decision that expires while it runs is judged again only on restart.
- A Markdown folder's `[[wikilinks]]` stay in the text: no relation joins a note to a note.

## Designed, not built

- Distribution as an OCI artifact (the files list as the config blob, so the descriptor digest is the pack digest),
  keyless signing from CI with SLSA provenance, Ed25519 keys.
- Trusted time (timestamp tokens judged, trust policy `timestampAuthorities`), in a later minor.
- Chunked Merkle file lists, streaming verification and lazy pulls beyond the 256 MiB default cap.
- Signed deltas that apply only onto their base digest.
- Readers in Python and TypeScript, a C ABI library with Python and Go bindings.
- Enforcement for the Protection, Code and Permissions profiles.
- The `retrieval` trial measure and the anchor kinds `key`, `entity` and `text`.
