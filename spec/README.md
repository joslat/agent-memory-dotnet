# The memory pack format: specification (draft)

This folder holds the first public draft of the memory pack format ("Ampack"), split into a small Core and separately
versioned profiles, with numbered requirements mapped to the conformance kit.

**Status:** draft. Format strings stay as they are today (`agentmemory.pack/1.2` and the rest); their public names are
decided later.

## Documents

| Document | Version | What it defines | Conformance classes |
|---|---|---|---|
| [core.md](core.md) | 1, draft | Container, paths, JSON profile, files list and digest, signatures, trust model, versioning, profiles, `pack.json`, kinds `knowledge` and `set`, records, card, attestation slot, limits | Core Reader, Core Writer |
| [profiles/walk.md](profiles/walk.md) | 1, draft | Module declarations, kinds `definition` and `snapshot`, the walk | Walk Host |
| [profiles/trial.md](profiles/trial.md) | 1, draft | Trials, the statistic, activation, upgrades, pack tests | Trial Host |
| [profiles/compose.md](profiles/compose.md) | 1, draft | Composites, members, locks, nesting, consent, grants | Compose Host |
| [profiles/certify.md](profiles/certify.md) | 1, draft | Evaluation certificates, the AEF run, certificate trust | Evaluator, Certify Host |
| [profiles/federation.md](profiles/federation.md) | 1, draft | Publisher WebIDs, audiences, publication receipts, the Solid binding | Federation Host |
| [profiles/protection.md](profiles/protection.md) | 1, draft | Protection levels P1 to P4: declaration only, refused until enforced | Protection Host |
| [profiles/code.md](profiles/code.md) | 1, draft | Code tiers E1 and E2: declaration only, refused until enforced | Code Host |
| [profiles/permissions.md](profiles/permissions.md) | 1, draft | Usage policies: declaration only, refused until enforced | Permissions Host |
| [host/trust-policy.md](host/trust-policy.md) | 1, draft | The host's trust policy and revocation documents | (every host) |
| [host/ledger.md](host/ledger.md) | 1, draft | The host's ledger against rollback | (every host) |
| [host/decision-record.md](host/decision-record.md) | 1, draft | The trial decision a host signs | Trial Host, Certify Host |
| [annex/status.md](annex/status.md) | informative | What the reference implementation builds today | |
| [annex/history.md](annex/history.md) | informative | Where the rules came from: versions, review rounds, dates | |

Machine-readable files:

- [registry.json](registry.json): the one list of every name the format defines (fields, values, roles, profiles and
  the rest). The tables of names in the documents are generated from it.
- [requirements.json](requirements.json): every requirement, with its document, section, level, a one-line summary and
  what verifies it.
- [vectors.json](vectors.json): for every vector of the conformance kit (`conformance/pack-v1.1/`), the requirements it
  tests.
- [render_tables.py](render_tables.py): rewrites the generated tables from the registry; `--check` fails when a table
  differs.

## Conformance classes

| Class | Implements | Can |
|---|---|---|
| Core Reader | Core | Verify and inspect any pack; report profiles it does not implement |
| Core Writer | Core | Build and sign packs a Core Reader verifies |
| Walk Host | Core, Walk | Load module packs and walk them |
| Trial Host | Core, Walk, Trial, the decision record | Switch packs on only on their own trial |
| Compose Host | Core, Compose | Load composites and hold packs together |
| Evaluator | Core, Trial, Certify | Issue evaluation certificates |
| Certify Host | Core, Trial, Certify, the decision record | Rely on trusted certificates in named namespaces |
| Federation Host | Core, Federation | Load federated packs and check placements |
| Protection, Code, Permissions Host | Their profile | Defined in a later version; today a conforming host refuses these packs |

A host loads a pack only when it implements every profile the pack uses. A pack's profiles are derived from what it
holds, by the registry's profile map (Core §13).

## Reading requirement ids

Every normative statement is a numbered requirement. Its id is written in bold at the start of its paragraph, in the form
`R-<AREA>-<nnn>`:

| Area | Document |
|---|---|
| `CORE` | core.md |
| `WALK`, `TRIAL`, `COMPOSE`, `CERTIFY`, `FED`, `PROT`, `CODE`, `PERM` | The profile of that name |
| `HOST` | The three host documents, numbered across them in the order trust policy, ledger, decision record |

Numbers run in steps of 1 within an area, in document order. The key words MUST, MUST NOT, SHOULD, SHOULD NOT and MAY are
used as BCP 14 defines them (Core §1.2). Rationale sits in "Why" notes, which are informative. The level recorded in
`requirements.json` is the strongest key word the requirement uses.

`verifiedBy` in `requirements.json` names what holds a requirement: `kit` (a conformance vector names it in
`vectors.json`), `unit:<Class>.<Method>` (a test in this repository's `tests/`), or `review` (no automatic check yet).

## Licence

Specification: CC BY 4.0 (draft).
